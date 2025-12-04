using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

var builder = WebApplication.CreateBuilder(args);

// Interview session tracking
var interviewSessions = new ConcurrentDictionary<string, InterviewSession>();
var completedInterviews = new ConcurrentDictionary<string, InterviewSession>(); // Store completed interviews
var interviewInvites = new ConcurrentDictionary<string, InterviewInvite>(); // Store invite codes
const int MaxQuestions = 3;
const int MaxDurationMinutes = 30;

// Azure Blob Storage client
var storageConnectionString = builder.Configuration["Azure:Storage:ConnectionString"];
var containerName = builder.Configuration["Azure:Storage:ContainerName"] ?? "interviews";
BlobContainerClient? blobContainerClient = null;

if (!string.IsNullOrEmpty(storageConnectionString))
{
    var blobServiceClient = new BlobServiceClient(storageConnectionString);
    blobContainerClient = blobServiceClient.GetBlobContainerClient(containerName);
    // Create container if it doesn't exist
    blobContainerClient.CreateIfNotExists(PublicAccessType.None);
}

// Register HttpClient factory
builder.Services.AddHttpClient();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Configure Kestrel for large file uploads (videos up to 500MB)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 500 * 1024 * 1024; // 500 MB
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
});

// Add Swagger/OpenAPI services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Interview Intelligence API",
     Version = "v1",
        Description = "API for interview intelligence with Azure Speech and OpenAI integration"
    });
});

var app = builder.Build();

// Configure Swagger middleware
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Interview Intelligence API v1");
   options.RoutePrefix = string.Empty; // Set Swagger UI at the app's root
  });
}

// Access configuration
var configuration = app.Configuration;

// Enable CORS
app.UseCors();

// ---------- Endpoints ----------

// Start a new interview session (with optional invite code)
app.MapPost("/api/interview/start", async (HttpContext http) =>
{
    // Try to read invite code from body
    string? inviteCode = null;
    try
    {
        using var reader = new StreamReader(http.Request.Body);
        var body = await reader.ReadToEndAsync();
        if (!string.IsNullOrEmpty(body))
        {
            var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("inviteCode", out var codeElement))
            {
                inviteCode = codeElement.GetString();
            }
        }
    }
    catch { /* No body or invalid JSON */ }
    
    var sessionId = Guid.NewGuid().ToString();
    var session = new InterviewSession
    {
        SessionId = sessionId,
        StartTime = DateTime.UtcNow,
        QuestionCount = 0,
        IsEnded = false
    };
    
    // Link to invite if provided
    if (!string.IsNullOrEmpty(inviteCode) && interviewInvites.TryGetValue(inviteCode.ToUpper(), out var invite))
    {
        if (invite.IsUsed)
        {
            return Results.BadRequest(new { error = "This invite code has already been used" });
        }
        if (invite.ExpiresAt < DateTime.UtcNow)
        {
            return Results.BadRequest(new { error = "This invite code has expired" });
        }
        
        session.InviteCode = inviteCode.ToUpper();
        session.CandidateName = invite.CandidateName;
        session.CandidateEmail = invite.CandidateEmail;
        session.Position = invite.Position;
        
        invite.IsUsed = true;
        invite.UsedAt = DateTime.UtcNow;
        invite.SessionId = sessionId;
    }
    
    interviewSessions[sessionId] = session;
    
    return Results.Ok(new { 
        sessionId, 
        message = "Interview session started",
        candidateName = session.CandidateName,
        position = session.Position
    });
})
.WithName("StartInterview")
.WithOpenApi(operation => new(operation)
{
    Summary = "Start a new interview session",
    Description = "Creates a new interview session and returns a session ID. Optionally accepts an invite code."
});

// Get interview session status
app.MapGet("/api/interview/status/{sessionId}", (string sessionId, HttpContext http) =>
{
    if (!interviewSessions.TryGetValue(sessionId, out var session))
    {
        return Results.NotFound(new { error = "Session not found" });
    }
    
    var elapsedMinutes = (DateTime.UtcNow - session.StartTime).TotalMinutes;
    var remainingQuestions = MaxQuestions - session.QuestionCount;
    var remainingMinutes = MaxDurationMinutes - elapsedMinutes;
    
    return Results.Ok(new
    {
        sessionId = session.SessionId,
        questionCount = session.QuestionCount,
        maxQuestions = MaxQuestions,
        elapsedMinutes = Math.Round(elapsedMinutes, 1),
        maxDurationMinutes = MaxDurationMinutes,
        remainingQuestions,
        remainingMinutes = Math.Round(remainingMinutes, 1),
        isEnded = session.IsEnded
    });
})
.WithName("GetInterviewStatus")
.WithOpenApi(operation => new(operation)
{
    Summary = "Get interview session status",
    Description = "Returns the current status of an interview session including question count and time elapsed"
});

// 1) Get Speech token
app.MapGet("/api/speech/token", async (HttpContext http, IHttpClientFactory httpClientFactory, IConfiguration config) =>
{
    var speechKey = config["Azure:Speech:Key"];
    var speechRegion = config["Azure:Speech:Region"];

    if (string.IsNullOrWhiteSpace(speechKey) || string.IsNullOrWhiteSpace(speechRegion))
    {
        http.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await http.Response.WriteAsJsonAsync(new { error = "Speech key/region not configured" });
   return;
    }

    var tokenEndpoint = $"https://{speechRegion}.api.cognitive.microsoft.com/sts/v1.0/issueToken";

    var client = httpClientFactory.CreateClient();
    client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", speechKey);

    var resp = await client.PostAsync(tokenEndpoint, content: null);
    if (!resp.IsSuccessStatusCode)
    {
        http.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await http.Response.WriteAsJsonAsync(new { error = "Failed to get speech token" });
      return;
    }

    var token = await resp.Content.ReadAsStringAsync();
    var result = new SpeechTokenResponse(token, speechRegion);

    await http.Response.WriteAsJsonAsync(result);
})
.WithName("GetSpeechToken")
.WithOpenApi(operation => new(operation)
{
 Summary = "Get Azure Speech Service token",
    Description = "Retrieves an authentication token for Azure Speech Service"
});

// 2) Generate follow-up question (using Azure OpenAI)
app.MapPost("/api/generate-question", async (GenerateQuestionRequest req, HttpContext http, IHttpClientFactory httpClientFactory, IConfiguration config) =>
{
    if (string.IsNullOrWhiteSpace(req.text))
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        await http.Response.WriteAsJsonAsync(new { error = "text is required" });
        return;
    }

    // Check session limits if sessionId is provided
    InterviewSession? session = null;
    if (!string.IsNullOrWhiteSpace(req.sessionId))
    {
        if (interviewSessions.TryGetValue(req.sessionId, out session))
        {
            var elapsedMinutes = (DateTime.UtcNow - session.StartTime).TotalMinutes;
            
            // Check if interview should end
            if (session.IsEnded || session.QuestionCount >= MaxQuestions || elapsedMinutes >= MaxDurationMinutes)
            {
                session.IsEnded = true;
                
                var thankYouMessage = $"Thank you so much for taking the time to speak with me today! I really enjoyed our conversation and learning about your experience with .NET development. {(session.QuestionCount >= MaxQuestions ? "We've covered everything I wanted to discuss" : "We've reached the end of our scheduled time")}. Our team will review everything and be in touch soon. Do you have any questions for me about the role or the team before we wrap up?";
                
                await http.Response.WriteAsJsonAsync(new GenerateQuestionResponse(thankYouMessage, true, session.QuestionCount, elapsedMinutes));
                return;
            }
        }
    }

    var aoKey = config["Azure:OpenAI:Key"];
    var aoEndpoint = config["Azure:OpenAI:Endpoint"];       // e.g. https://your-resource.openai.azure.com
    var deploymentId = config["Azure:OpenAI:DeploymentId"]; // model deployment name

    if (string.IsNullOrWhiteSpace(aoKey) ||
        string.IsNullOrWhiteSpace(aoEndpoint) ||
        string.IsNullOrWhiteSpace(deploymentId))
    {
        http.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await http.Response.WriteAsJsonAsync(new { error = "Azure OpenAI config missing" });
        return;
    }

    var systemPrompt = @"You are a friendly and professional technical interviewer conducting an interview for a .NET developer position.

TONE & STYLE:
- Be warm, encouraging, and professionally friendly
- Always start with a brief, positive acknowledgment of the candidate's answer (1 short sentence)
- Then ask your follow-up question
- Keep your total response concise (2-3 sentences max)

TOPIC GUIDELINES:
- Focus on .NET technologies (C#, ASP.NET, .NET Core, Entity Framework, LINQ, etc.)
- You may also discuss general software development practices, problem-solving, and teamwork
- Stay within the context of the current interview conversation
- If the candidate goes off-topic, gently and kindly redirect back to relevant topics

EXAMPLE FORMAT:
'That's a great point about [topic]! [Follow-up question about .NET or the interview context]'
or
'I appreciate you sharing that experience. [Follow-up question]'";

    var userPrompt = $@"Given the candidate's last answer, provide a brief positive acknowledgment followed by a relevant follow-up question.
Candidate answer: ""{req.text}""
Remember: Start with short feedback (1 sentence), then ask your question. Be warm and professional.";

    var aoUrl = $"{aoEndpoint}/openai/deployments/{deploymentId}/chat/completions?api-version=2023-10-01-preview";

    var body = new
    {
        model = "gpt-4o", // or omit if your deployment maps model internally
        messages = new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt }
        },
        max_tokens = 100,
        temperature = 0.7
    };

    var jsonBody = JsonSerializer.Serialize(body);

    var client = httpClientFactory.CreateClient();
    // Azure OpenAI expects the `api-key` header
    client.DefaultRequestHeaders.Clear();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aoKey);
    client.DefaultRequestHeaders.Add("api-key", aoKey);

    var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

    var response = await client.PostAsync(aoUrl, content);
    if (!response.IsSuccessStatusCode)
    {
    var errorText = await response.Content.ReadAsStringAsync();
        http.Response.StatusCode = StatusCodes.Status500InternalServerError;
   await http.Response.WriteAsJsonAsync(new { error = "Failed to call Azure OpenAI", details = errorText });
   return;
    }

    var responseJson = await response.Content.ReadAsStringAsync();

    using var doc = JsonDocument.Parse(responseJson);
    var root = doc.RootElement;

    string question = "Can you elaborate?";

    try
    {
        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() > 0)
        {
            var message = choices[0].GetProperty("message");
            question = message.GetProperty("content").GetString() ?? question;
        }
    }
    catch
    {
        // fall back to default question
    }

    // Increment question count and auto-save transcript if session exists
    if (session != null)
    {
        // Save the candidate's answer with the previous question (if exists)
        if (session.Transcript.Count > 0 || session.QuestionCount == 0)
        {
            // Get the last question asked (or use intro for first answer)
            var lastQuestion = session.Transcript.Count > 0 
                ? session.Transcript.Last().Question 
                : "Tell me about yourself and your experience.";
            
            // If this is a new answer, save it
            var exchange = new QAExchange
            {
                QuestionNumber = session.QuestionCount + 1,
                Question = lastQuestion,
                Answer = req.text,
                Timestamp = DateTime.UtcNow,
                ResponseTimeSeconds = 0 // Will be calculated client-side if needed
            };
            session.Transcript.Add(exchange);
        }
        
        // Store the new question for next answer
        session.LastQuestionAsked = question.Trim();
        
        session.QuestionCount++;
        var elapsedMinutes = (DateTime.UtcNow - session.StartTime).TotalMinutes;
        
        var result = new GenerateQuestionResponse(question.Trim(), false, session.QuestionCount, elapsedMinutes);
        await http.Response.WriteAsJsonAsync(result);
        return;
    }

    await http.Response.WriteAsJsonAsync(new GenerateQuestionResponse(question.Trim(), false, null, null));
})
.WithName("GenerateQuestion")
.WithOpenApi(operation => new(operation)
{
    Summary = "Generate follow-up interview question",
    Description = "Uses Azure OpenAI to generate a follow-up question based on the candidate's answer"
});

// 3) Get ICE server credentials for WebRTC avatar connection
app.MapGet("/api/speech/ice", async (HttpContext http, IHttpClientFactory httpClientFactory, IConfiguration config) =>
{
    var speechKey = config["Azure:Speech:Key"];
    var speechRegion = config["Azure:Speech:Region"];

    if (string.IsNullOrWhiteSpace(speechKey) || string.IsNullOrWhiteSpace(speechRegion))
    {
        http.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await http.Response.WriteAsJsonAsync(new { error = "Speech key/region not configured" });
        return;
    }

    var iceEndpoint = $"https://{speechRegion}.tts.speech.microsoft.com/cognitiveservices/avatar/relay/token/v1";

    var client = httpClientFactory.CreateClient();
    client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", speechKey);

    var resp = await client.GetAsync(iceEndpoint);
    if (!resp.IsSuccessStatusCode)
    {
        var errorText = await resp.Content.ReadAsStringAsync();
        http.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await http.Response.WriteAsJsonAsync(new { error = "Failed to get ICE credentials", details = errorText });
        return;
    }

    var iceJson = await resp.Content.ReadAsStringAsync();
    http.Response.ContentType = "application/json";
    await http.Response.WriteAsync(iceJson);
})
.WithName("GetIceCredentials")
.WithOpenApi(operation => new(operation)
{
    Summary = "Get ICE server credentials for avatar WebRTC",
    Description = "Retrieves ICE server URL, username, and credential for establishing WebRTC connection with the avatar service"
});

// 4) Save Q&A exchange to transcript
app.MapPost("/api/interview/transcript", (SaveTranscriptRequest req, HttpContext http) =>
{
    if (string.IsNullOrWhiteSpace(req.sessionId))
    {
        return Results.BadRequest(new { error = "sessionId is required" });
    }
    
    if (!interviewSessions.TryGetValue(req.sessionId, out var session))
    {
        return Results.NotFound(new { error = "Session not found" });
    }
    
    var exchange = new QAExchange
    {
        QuestionNumber = session.Transcript.Count + 1,
        Question = req.question,
        Answer = req.answer,
        Timestamp = DateTime.UtcNow,
        ResponseTimeSeconds = req.responseTimeSeconds
    };
    
    session.Transcript.Add(exchange);
    
    return Results.Ok(new { 
        message = "Transcript saved", 
        exchangeCount = session.Transcript.Count 
    });
})
.WithName("SaveTranscript")
.WithOpenApi(operation => new(operation)
{
    Summary = "Save Q&A exchange to interview transcript",
    Description = "Saves a question and answer pair to the interview session for later analysis"
});

// 5) Get full interview transcript
app.MapGet("/api/interview/transcript/{sessionId}", (string sessionId, HttpContext http) =>
{
    if (!interviewSessions.TryGetValue(sessionId, out var session))
    {
        return Results.NotFound(new { error = "Session not found" });
    }
    
    return Results.Ok(new
    {
        sessionId = session.SessionId,
        startTime = session.StartTime,
        questionCount = session.QuestionCount,
        isEnded = session.IsEnded,
        transcript = session.Transcript,
        analysis = session.Analysis
    });
})
.WithName("GetTranscript")
.WithOpenApi(operation => new(operation)
{
    Summary = "Get interview transcript",
    Description = "Retrieves the full transcript and analysis for an interview session"
});

// 6) Analyze interview and generate score/feedback
app.MapPost("/api/interview/analyze", async (AnalyzeInterviewRequest req, HttpContext http, IHttpClientFactory httpClientFactory, IConfiguration config) =>
{
    if (string.IsNullOrWhiteSpace(req.sessionId))
    {
        return Results.BadRequest(new { error = "sessionId is required" });
    }
    
    if (!interviewSessions.TryGetValue(req.sessionId, out var session))
    {
        return Results.NotFound(new { error = "Session not found" });
    }
    
    if (session.Transcript.Count == 0)
    {
        return Results.BadRequest(new { error = "No transcript data to analyze" });
    }
    
    var aoKey = config["Azure:OpenAI:Key"];
    var aoEndpoint = config["Azure:OpenAI:Endpoint"];
    var deploymentId = config["Azure:OpenAI:DeploymentId"];
    
    if (string.IsNullOrWhiteSpace(aoKey) || string.IsNullOrWhiteSpace(aoEndpoint) || string.IsNullOrWhiteSpace(deploymentId))
    {
        return Results.StatusCode(500);
    }
    
    // Build transcript text for analysis
    var transcriptText = new StringBuilder();
    transcriptText.AppendLine("INTERVIEW TRANSCRIPT:");
    transcriptText.AppendLine("=====================");
    foreach (var qa in session.Transcript)
    {
        transcriptText.AppendLine($"\nQ{qa.QuestionNumber}: {qa.Question}");
        transcriptText.AppendLine($"A{qa.QuestionNumber}: {qa.Answer}");
        transcriptText.AppendLine($"(Response time: {qa.ResponseTimeSeconds:F1} seconds)");
    }
    
    var systemPrompt = @"You are an expert technical interviewer and hiring manager. Analyze the following interview transcript for a .NET developer position and provide a comprehensive assessment.

Return your analysis as a JSON object with this exact structure:
{
    ""overallScore"": <number 1-100>,
    ""overallFeedback"": ""<2-3 sentence summary>"",
    ""technicalSkills"": {
        ""score"": <number 1-100>,
        ""feedback"": ""<assessment of technical knowledge>"",
        ""topicsDiscussed"": [""topic1"", ""topic2""]
    },
    ""communication"": {
        ""score"": <number 1-100>,
        ""clarity"": ""<assessment of clarity>"",
        ""articulation"": ""<assessment of articulation>""
    },
    ""strengths"": [
        {""area"": ""<strength area>"", ""description"": ""<details>""}
    ],
    ""areasForImprovement"": [
        {""area"": ""<improvement area>"", ""description"": ""<details>""}
    ],
    ""hiringRecommendation"": ""<Strong Hire / Hire / Maybe / No Hire with brief justification>""
}

Be constructive and specific. Base scores on demonstrated knowledge and communication quality.";

    var userPrompt = $@"{transcriptText}

Analyze this interview and provide your assessment as JSON.";

    var aoUrl = $"{aoEndpoint}/openai/deployments/{deploymentId}/chat/completions?api-version=2023-10-01-preview";
    
    var body = new
    {
        messages = new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt }
        },
        max_tokens = 1500,
        temperature = 0.3
    };
    
    var jsonBody = JsonSerializer.Serialize(body);
    var client = httpClientFactory.CreateClient();
    client.DefaultRequestHeaders.Clear();
    client.DefaultRequestHeaders.Add("api-key", aoKey);
    
    var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
    var response = await client.PostAsync(aoUrl, content);
    
    if (!response.IsSuccessStatusCode)
    {
        var errorText = await response.Content.ReadAsStringAsync();
        return Results.StatusCode(500);
    }
    
    var responseJson = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(responseJson);
    var root = doc.RootElement;
    
    try
    {
        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() > 0)
        {
            var messageContent = choices[0].GetProperty("message").GetProperty("content").GetString();
            
            // Clean up the response (remove markdown code blocks if present)
            messageContent = messageContent?.Trim();
            if (messageContent?.StartsWith("```json") == true)
            {
                messageContent = messageContent.Substring(7);
            }
            if (messageContent?.StartsWith("```") == true)
            {
                messageContent = messageContent.Substring(3);
            }
            if (messageContent?.EndsWith("```") == true)
            {
                messageContent = messageContent.Substring(0, messageContent.Length - 3);
            }
            messageContent = messageContent?.Trim();
            
            // Parse the analysis JSON
            var analysisJson = JsonDocument.Parse(messageContent!);
            var analysisRoot = analysisJson.RootElement;
            
            var analysis = new InterviewAnalysis
            {
                OverallScore = analysisRoot.GetProperty("overallScore").GetInt32(),
                OverallFeedback = analysisRoot.GetProperty("overallFeedback").GetString() ?? "",
                TechnicalSkills = new TechnicalAssessment
                {
                    Score = analysisRoot.GetProperty("technicalSkills").GetProperty("score").GetInt32(),
                    Feedback = analysisRoot.GetProperty("technicalSkills").GetProperty("feedback").GetString() ?? "",
                    TopicsDiscussed = analysisRoot.GetProperty("technicalSkills").GetProperty("topicsDiscussed")
                        .EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                },
                Communication = new CommunicationAssessment
                {
                    Score = analysisRoot.GetProperty("communication").GetProperty("score").GetInt32(),
                    Clarity = analysisRoot.GetProperty("communication").GetProperty("clarity").GetString() ?? "",
                    Articulation = analysisRoot.GetProperty("communication").GetProperty("articulation").GetString() ?? ""
                },
                Strengths = analysisRoot.GetProperty("strengths").EnumerateArray().Select(x => new StrengthWeakness
                {
                    Area = x.GetProperty("area").GetString() ?? "",
                    Description = x.GetProperty("description").GetString() ?? ""
                }).ToList(),
                AreasForImprovement = analysisRoot.GetProperty("areasForImprovement").EnumerateArray().Select(x => new StrengthWeakness
                {
                    Area = x.GetProperty("area").GetString() ?? "",
                    Description = x.GetProperty("description").GetString() ?? ""
                }).ToList(),
                HiringRecommendation = analysisRoot.GetProperty("hiringRecommendation").GetString() ?? "",
                AnalyzedAt = DateTime.UtcNow
            };
            
            session.Analysis = analysis;
            
            return Results.Ok(new
            {
                sessionId = session.SessionId,
                analysis = analysis,
                transcriptCount = session.Transcript.Count,
                interviewDuration = (DateTime.UtcNow - session.StartTime).TotalMinutes
            });
        }
    }
    catch (Exception ex)
    {
        return Results.StatusCode(500);
    }
    
    return Results.StatusCode(500);
})
.WithName("AnalyzeInterview")
.WithOpenApi(operation => new(operation)
{
    Summary = "Analyze interview and generate score/feedback",
    Description = "Uses AI to analyze the interview transcript and provide comprehensive scoring and feedback"
});

// ========== ADMIN ENDPOINTS (Recruiter Dashboard) ==========

// ===== INVITE CODE MANAGEMENT =====

// Generate a new invite code
app.MapPost("/api/admin/invites", async (HttpContext http) =>
{
    try
    {
        using var reader = new StreamReader(http.Request.Body);
        var body = await reader.ReadToEndAsync();
        var json = JsonDocument.Parse(body);
        
        var candidateName = json.RootElement.GetProperty("candidateName").GetString() ?? "";
        var candidateEmail = json.RootElement.TryGetProperty("candidateEmail", out var emailEl) ? emailEl.GetString() : "";
        var position = json.RootElement.TryGetProperty("position", out var posEl) ? posEl.GetString() : "Software Developer";
        var expiresInDays = json.RootElement.TryGetProperty("expiresInDays", out var expEl) ? expEl.GetInt32() : 7;
        
        // Generate a unique 6-character code
        var code = GenerateInviteCode();
        while (interviewInvites.ContainsKey(code))
        {
            code = GenerateInviteCode();
        }
        
        var invite = new InterviewInvite
        {
            Code = code,
            CandidateName = candidateName,
            CandidateEmail = candidateEmail,
            Position = position,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(expiresInDays),
            IsUsed = false
        };
        
        interviewInvites[code] = invite;
        
        return Results.Ok(new
        {
            code = invite.Code,
            candidateName = invite.CandidateName,
            candidateEmail = invite.CandidateEmail,
            position = invite.Position,
            expiresAt = invite.ExpiresAt,
            interviewUrl = $"https://your-domain.com?code={invite.Code}" // Frontend will use this
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = "Invalid request body", details = ex.Message });
    }
})
.WithName("CreateInvite")
.WithOpenApi(operation => new(operation)
{
    Summary = "Generate a new interview invite code",
    Description = "Creates a unique invite code for a candidate to use when starting their interview"
});

// Get all invites
app.MapGet("/api/admin/invites", () =>
{
    var invites = interviewInvites.Values
        .OrderByDescending(i => i.CreatedAt)
        .Select(i => new
        {
            code = i.Code,
            candidateName = i.CandidateName,
            candidateEmail = i.CandidateEmail,
            position = i.Position,
            createdAt = i.CreatedAt,
            expiresAt = i.ExpiresAt,
            isUsed = i.IsUsed,
            usedAt = i.UsedAt,
            sessionId = i.SessionId,
            isExpired = i.ExpiresAt < DateTime.UtcNow
        });
    
    return Results.Ok(invites);
})
.WithName("GetAllInvites")
.WithOpenApi(operation => new(operation)
{
    Summary = "Get all interview invites",
    Description = "Returns all generated invite codes with their status"
});

// Validate an invite code (for frontend to check before joining)
app.MapGet("/api/invite/validate/{code}", (string code) =>
{
    if (string.IsNullOrEmpty(code))
    {
        return Results.BadRequest(new { valid = false, error = "No code provided" });
    }
    
    var upperCode = code.ToUpper();
    if (!interviewInvites.TryGetValue(upperCode, out var invite))
    {
        return Results.NotFound(new { valid = false, error = "Invalid invite code" });
    }
    
    if (invite.IsUsed)
    {
        return Results.Ok(new { valid = false, error = "This invite code has already been used" });
    }
    
    if (invite.ExpiresAt < DateTime.UtcNow)
    {
        return Results.Ok(new { valid = false, error = "This invite code has expired" });
    }
    
    return Results.Ok(new
    {
        valid = true,
        candidateName = invite.CandidateName,
        position = invite.Position
    });
})
.WithName("ValidateInvite")
.WithOpenApi(operation => new(operation)
{
    Summary = "Validate an invite code",
    Description = "Checks if an invite code is valid and returns candidate info"
});

// Delete an invite
app.MapDelete("/api/admin/invites/{code}", (string code) =>
{
    var upperCode = code.ToUpper();
    if (interviewInvites.TryRemove(upperCode, out _))
    {
        return Results.Ok(new { message = "Invite deleted" });
    }
    return Results.NotFound(new { error = "Invite not found" });
})
.WithName("DeleteInvite")
.WithOpenApi(operation => new(operation)
{
    Summary = "Delete an invite code",
    Description = "Removes an invite code from the system"
});

// ===== INTERVIEW MANAGEMENT =====

// Get all completed interviews (for admin dashboard)
app.MapGet("/api/admin/interviews", () =>
{
    var allInterviews = new List<object>();
    
    // Add completed interviews
    foreach (var session in completedInterviews.Values)
    {
        allInterviews.Add(new
        {
            sessionId = session.SessionId,
            inviteCode = session.InviteCode,
            candidateName = session.CandidateName ?? "Unknown",
            candidateEmail = session.CandidateEmail,
            position = session.Position ?? "Software Developer",
            startTime = session.StartTime,
            endTime = session.IsEnded ? session.StartTime.AddMinutes((DateTime.UtcNow - session.StartTime).TotalMinutes) : (DateTime?)null,
            questionCount = session.QuestionCount,
            status = session.IsEnded ? "Completed" : "In Progress",
            hasAnalysis = session.Analysis != null,
            overallScore = session.Analysis?.OverallScore,
            hiringRecommendation = session.Analysis?.HiringRecommendation
        });
    }
    
    // Add active interviews
    foreach (var session in interviewSessions.Values)
    {
        if (!completedInterviews.ContainsKey(session.SessionId))
        {
            allInterviews.Add(new
            {
                sessionId = session.SessionId,
                inviteCode = session.InviteCode,
                candidateName = session.CandidateName ?? "Unknown",
                candidateEmail = session.CandidateEmail,
                position = session.Position ?? "Software Developer",
                startTime = session.StartTime,
                endTime = (DateTime?)null,
                questionCount = session.QuestionCount,
                status = "In Progress",
                hasAnalysis = false,
                overallScore = (int?)null,
                hiringRecommendation = (string?)null
            });
        }
    }
    
    return Results.Ok(allInterviews.OrderByDescending(i => ((dynamic)i).startTime));
})
.WithName("GetAllInterviews")
.WithOpenApi(operation => new(operation)
{
    Summary = "Get all interviews for admin dashboard",
    Description = "Returns all completed and in-progress interviews for recruiters"
});

// Get detailed interview with analysis
app.MapGet("/api/admin/interviews/{sessionId}", (string sessionId) =>
{
    InterviewSession? session = null;
    
    if (completedInterviews.TryGetValue(sessionId, out session) || 
        interviewSessions.TryGetValue(sessionId, out session))
    {
        return Results.Ok(new
        {
            sessionId = session.SessionId,
            candidateName = session.CandidateName ?? "Unknown",
            position = session.Position ?? "Software Developer",
            startTime = session.StartTime,
            questionCount = session.QuestionCount,
            isEnded = session.IsEnded,
            transcript = session.Transcript,
            analysis = session.Analysis
        });
    }
    
    return Results.NotFound(new { error = "Interview not found" });
})
.WithName("GetInterviewDetail")
.WithOpenApi(operation => new(operation)
{
    Summary = "Get detailed interview data including transcript and analysis",
    Description = "Returns full interview details for admin review"
});

// Get aggregated analytics
app.MapGet("/api/admin/analytics", () =>
{
    var allSessions = completedInterviews.Values.ToList();
    var analyzedSessions = allSessions.Where(s => s.Analysis != null).ToList();
    
    var scoreDistribution = new Dictionary<string, int>
    {
        { "Excellent (80-100)", analyzedSessions.Count(s => s.Analysis!.OverallScore >= 80) },
        { "Good (60-79)", analyzedSessions.Count(s => s.Analysis!.OverallScore >= 60 && s.Analysis!.OverallScore < 80) },
        { "Fair (40-59)", analyzedSessions.Count(s => s.Analysis!.OverallScore >= 40 && s.Analysis!.OverallScore < 60) },
        { "Poor (0-39)", analyzedSessions.Count(s => s.Analysis!.OverallScore < 40) }
    };
    
    var recommendationBreakdown = new Dictionary<string, int>
    {
        { "Strong Hire", analyzedSessions.Count(s => s.Analysis!.HiringRecommendation.Contains("Strong", StringComparison.OrdinalIgnoreCase) && s.Analysis!.HiringRecommendation.Contains("Hire", StringComparison.OrdinalIgnoreCase)) },
        { "Hire", analyzedSessions.Count(s => s.Analysis!.HiringRecommendation.Contains("Hire", StringComparison.OrdinalIgnoreCase) && !s.Analysis!.HiringRecommendation.Contains("Strong", StringComparison.OrdinalIgnoreCase) && !s.Analysis!.HiringRecommendation.Contains("No", StringComparison.OrdinalIgnoreCase)) },
        { "Maybe", analyzedSessions.Count(s => s.Analysis!.HiringRecommendation.Contains("Maybe", StringComparison.OrdinalIgnoreCase) || s.Analysis!.HiringRecommendation.Contains("Consider", StringComparison.OrdinalIgnoreCase)) },
        { "No Hire", analyzedSessions.Count(s => s.Analysis!.HiringRecommendation.Contains("No Hire", StringComparison.OrdinalIgnoreCase) || s.Analysis!.HiringRecommendation.Contains("Reject", StringComparison.OrdinalIgnoreCase)) }
    };
    
    return Results.Ok(new
    {
        totalInterviews = allSessions.Count,
        analyzedInterviews = analyzedSessions.Count,
        inProgressInterviews = interviewSessions.Values.Count(s => !s.IsEnded),
        averageScore = analyzedSessions.Any() ? Math.Round(analyzedSessions.Average(s => s.Analysis!.OverallScore), 1) : 0,
        averageTechnicalScore = analyzedSessions.Any() ? Math.Round(analyzedSessions.Average(s => s.Analysis!.TechnicalSkills.Score), 1) : 0,
        averageCommunicationScore = analyzedSessions.Any() ? Math.Round(analyzedSessions.Average(s => s.Analysis!.Communication.Score), 1) : 0,
        averageQuestionsAsked = allSessions.Any() ? Math.Round(allSessions.Average(s => s.QuestionCount), 1) : 0,
        scoreDistribution = scoreDistribution,
        recommendationBreakdown = recommendationBreakdown,
        recentInterviews = allSessions
            .OrderByDescending(s => s.StartTime)
            .Take(5)
            .Select(s => new
            {
                sessionId = s.SessionId,
                candidateName = s.CandidateName ?? "Unknown",
                startTime = s.StartTime,
                score = s.Analysis?.OverallScore,
                recommendation = s.Analysis?.HiringRecommendation
            })
    });
})
.WithName("GetAnalytics")
.WithOpenApi(operation => new(operation)
{
    Summary = "Get aggregated interview analytics",
    Description = "Returns analytics data for dashboard charts and metrics"
});

// End interview and move to completed (called when interview ends)
app.MapPost("/api/admin/interviews/{sessionId}/complete", async (string sessionId) =>
{
    if (interviewSessions.TryGetValue(sessionId, out var session))
    {
        session.IsEnded = true;
        completedInterviews[sessionId] = session;
        
        // Auto-save transcript to blob storage
        if (blobContainerClient != null && session.Transcript.Count > 0)
        {
            try
            {
                var transcriptJson = JsonSerializer.Serialize(new
                {
                    sessionId = session.SessionId,
                    candidateName = session.CandidateName,
                    position = session.Position,
                    startTime = session.StartTime,
                    endTime = DateTime.UtcNow,
                    questionCount = session.QuestionCount,
                    transcript = session.Transcript
                }, new JsonSerializerOptions { WriteIndented = true });
                
                var blobName = $"transcripts/{sessionId}.json";
                var blobClient = blobContainerClient.GetBlobClient(blobName);
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(transcriptJson));
                await blobClient.UploadAsync(stream, overwrite: true);
                
                session.TranscriptBlobUrl = blobClient.Uri.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save transcript to blob: {ex.Message}");
            }
        }
        
        return Results.Ok(new { message = "Interview marked as complete", sessionId = sessionId });
    }
    
    return Results.NotFound(new { error = "Interview session not found" });
})
.WithName("CompleteInterview")
.WithOpenApi(operation => new(operation)
{
    Summary = "Mark interview as complete",
    Description = "Moves interview from active to completed state"
});

// ========== BLOB STORAGE ENDPOINTS ==========

// Upload video recording
app.MapPost("/api/storage/upload-video/{sessionId}", async (string sessionId, HttpRequest request) =>
{
    if (blobContainerClient == null)
    {
        return Results.StatusCode(503); // Service unavailable - storage not configured
    }
    
    try
    {
        // Read the video file from the request body
        using var memoryStream = new MemoryStream();
        await request.Body.CopyToAsync(memoryStream);
        memoryStream.Position = 0;
        
        var contentType = request.ContentType ?? "video/webm";
        var extension = contentType.Contains("mp4") ? "mp4" : "webm";
        var blobName = $"videos/{sessionId}.{extension}";
        
        var blobClient = blobContainerClient.GetBlobClient(blobName);
        await blobClient.UploadAsync(memoryStream, new BlobHttpHeaders { ContentType = contentType });
        
        // Update session with video URL
        if (interviewSessions.TryGetValue(sessionId, out var session))
        {
            session.VideoBlobUrl = blobClient.Uri.ToString();
        }
        else if (completedInterviews.TryGetValue(sessionId, out var completedSession))
        {
            completedSession.VideoBlobUrl = blobClient.Uri.ToString();
        }
        
        return Results.Ok(new { 
            message = "Video uploaded successfully", 
            blobUrl = blobClient.Uri.ToString(),
            sessionId = sessionId
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Video upload failed: {ex.Message}");
        return Results.StatusCode(500);
    }
})
.WithName("UploadVideo")
.WithOpenApi(operation => new(operation)
{
    Summary = "Upload interview video recording",
    Description = "Uploads the candidate's video recording to Azure Blob Storage"
});

// Get video URL for playback (generates SAS token for secure access)
app.MapGet("/api/storage/video/{sessionId}", (string sessionId) =>
{
    if (blobContainerClient == null)
    {
        return Results.StatusCode(503);
    }
    
    InterviewSession? session = null;
    if (!completedInterviews.TryGetValue(sessionId, out session))
    {
        interviewSessions.TryGetValue(sessionId, out session);
    }
    
    if (session?.VideoBlobUrl == null)
    {
        return Results.NotFound(new { error = "Video not found for this session" });
    }
    
    // Return the blob URL (in production, you'd generate a SAS token for secure access)
    return Results.Ok(new { 
        videoUrl = session.VideoBlobUrl,
        sessionId = sessionId
    });
})
.WithName("GetVideoUrl")
.WithOpenApi(operation => new(operation)
{
    Summary = "Get video playback URL",
    Description = "Returns the URL for playing back the interview recording"
});

// Get transcript from blob storage
app.MapGet("/api/storage/transcript/{sessionId}", async (string sessionId) =>
{
    if (blobContainerClient == null)
    {
        return Results.StatusCode(503);
    }
    
    try
    {
        var blobName = $"transcripts/{sessionId}.json";
        var blobClient = blobContainerClient.GetBlobClient(blobName);
        
        if (!await blobClient.ExistsAsync())
        {
            return Results.NotFound(new { error = "Transcript not found" });
        }
        
        var response = await blobClient.DownloadContentAsync();
        var content = response.Value.Content.ToString();
        var transcript = JsonSerializer.Deserialize<object>(content);
        
        return Results.Ok(transcript);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to retrieve transcript: {ex.Message}");
        return Results.StatusCode(500);
    }
})
.WithName("GetTranscriptFromStorage")
.WithOpenApi(operation => new(operation)
{
    Summary = "Get transcript from blob storage",
    Description = "Retrieves the saved transcript JSON from Azure Blob Storage"
});

// Check storage status
app.MapGet("/api/storage/status", () =>
{
    return Results.Ok(new { 
        configured = blobContainerClient != null,
        containerName = containerName
    });
})
.WithName("StorageStatus")
.WithOpenApi(operation => new(operation)
{
    Summary = "Check blob storage status",
    Description = "Returns whether blob storage is properly configured"
});

app.Run();

// ---------- Helper Functions ----------
static string GenerateInviteCode()
{
    const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Removed confusing chars like 0, O, 1, I
    var random = new Random();
    return new string(Enumerable.Repeat(chars, 6).Select(s => s[random.Next(s.Length)]).ToArray());
}

// ---------- Models ----------
public record SpeechTokenResponse(string token, string region);
public record GenerateQuestionRequest(string text, string? sessionId = null);
public record GenerateQuestionResponse(string question, bool isInterviewEnded = false, int? questionCount = null, double? elapsedMinutes = null);

public class InterviewInvite
{
    public string Code { get; set; } = string.Empty;
    public string CandidateName { get; set; } = string.Empty;
    public string? CandidateEmail { get; set; }
    public string? Position { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsUsed { get; set; }
    public DateTime? UsedAt { get; set; }
    public string? SessionId { get; set; } // Links to the interview session when used
}

public class InterviewSession
{
    public string SessionId { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public int QuestionCount { get; set; }
    public bool IsEnded { get; set; }
    public List<QAExchange> Transcript { get; set; } = new();
    public string? CandidateName { get; set; }
    public string? CandidateEmail { get; set; }
    public string? Position { get; set; }
    public string? InviteCode { get; set; } // Links back to invite
    public InterviewAnalysis? Analysis { get; set; }
    public string? LastQuestionAsked { get; set; } // Track the last question for auto-save
    public string? VideoBlobUrl { get; set; } // URL to video recording in blob storage
    public string? TranscriptBlobUrl { get; set; } // URL to transcript JSON in blob storage
}

public class QAExchange
{
    public int QuestionNumber { get; set; }
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public double ResponseTimeSeconds { get; set; }
}

public class InterviewAnalysis
{
    public int OverallScore { get; set; } // 1-100
    public string OverallFeedback { get; set; } = string.Empty;
    public TechnicalAssessment TechnicalSkills { get; set; } = new();
    public CommunicationAssessment Communication { get; set; } = new();
    public List<StrengthWeakness> Strengths { get; set; } = new();
    public List<StrengthWeakness> AreasForImprovement { get; set; } = new();
    public string HiringRecommendation { get; set; } = string.Empty;
    public DateTime AnalyzedAt { get; set; }
}

public class TechnicalAssessment
{
    public int Score { get; set; } // 1-100
    public string Feedback { get; set; } = string.Empty;
    public List<string> TopicsDiscussed { get; set; } = new();
}

public class CommunicationAssessment
{
    public int Score { get; set; } // 1-100
    public string Clarity { get; set; } = string.Empty;
    public string Articulation { get; set; } = string.Empty;
}

public class StrengthWeakness
{
    public string Area { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public record AnalyzeInterviewRequest(string sessionId);
public record SaveTranscriptRequest(string sessionId, string question, string answer, double responseTimeSeconds);

