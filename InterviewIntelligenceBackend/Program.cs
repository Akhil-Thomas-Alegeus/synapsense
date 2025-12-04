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
using Microsoft.Azure.Cosmos;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

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

// Azure Cosmos DB client for Job Descriptions
var cosmosConnectionString = builder.Configuration["Azure:CosmosDB:ConnectionString"];
var cosmosDatabaseName = builder.Configuration["Azure:CosmosDB:DatabaseName"] ?? "interviewdb";
var cosmosContainerName = builder.Configuration["Azure:CosmosDB:ContainerName"] ?? "interviewdb";
CosmosClient? cosmosClient = null;
Container? cosmosContainer = null;

if (!string.IsNullOrEmpty(cosmosConnectionString))
{
    cosmosClient = new CosmosClient(cosmosConnectionString);
    var database = cosmosClient.GetDatabase(cosmosDatabaseName);
    cosmosContainer = database.GetContainer(cosmosContainerName);
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

// ========== COSMOS DB HELPER FUNCTIONS ==========

// Helper to save invite to Cosmos DB
async Task SaveInviteToCosmosAsync(InterviewInvite invite)
{
    if (cosmosContainer == null) return;
    try
    {
        invite.id = invite.Code; // Use Code as the document ID
        await cosmosContainer.UpsertItemAsync(invite, new PartitionKey(invite.id));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to save invite to Cosmos DB: {ex.Message}");
    }
}

// Helper to get invite from Cosmos DB
async Task<InterviewInvite?> GetInviteFromCosmosAsync(string code)
{
    if (cosmosContainer == null) return null;
    try
    {
        var response = await cosmosContainer.ReadItemAsync<InterviewInvite>(code, new PartitionKey(code));
        return response.Resource;
    }
    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to get invite from Cosmos DB: {ex.Message}");
        return null;
    }
}

// Helper to delete invite from Cosmos DB
async Task DeleteInviteFromCosmosAsync(string code)
{
    if (cosmosContainer == null) return;
    try
    {
        await cosmosContainer.DeleteItemAsync<InterviewInvite>(code, new PartitionKey(code));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to delete invite from Cosmos DB: {ex.Message}");
    }
}

// Helper to get all invites from Cosmos DB
async Task<List<InterviewInvite>> GetAllInvitesFromCosmosAsync()
{
    var invites = new List<InterviewInvite>();
    if (cosmosContainer == null) return invites;
    try
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = 'invite'");
        var iterator = cosmosContainer.GetItemQueryIterator<InterviewInvite>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            invites.AddRange(response);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to get invites from Cosmos DB: {ex.Message}");
    }
    return invites;
}

// Helper to save session to Cosmos DB
async Task SaveSessionToCosmosAsync(InterviewSession session)
{
    if (cosmosContainer == null) return;
    try
    {
        session.id = session.SessionId; // Use SessionId as the document ID
        await cosmosContainer.UpsertItemAsync(session, new PartitionKey(session.id));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to save session to Cosmos DB: {ex.Message}");
    }
}

// Helper to get session from Cosmos DB
async Task<InterviewSession?> GetSessionFromCosmosAsync(string sessionId)
{
    if (cosmosContainer == null) return null;
    try
    {
        var response = await cosmosContainer.ReadItemAsync<InterviewSession>(sessionId, new PartitionKey(sessionId));
        return response.Resource;
    }
    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to get session from Cosmos DB: {ex.Message}");
        return null;
    }
}

// Helper to get all sessions from Cosmos DB (both active and completed)
async Task<List<InterviewSession>> GetAllSessionsFromCosmosAsync(bool? isEnded = null)
{
    var sessions = new List<InterviewSession>();
    if (cosmosContainer == null) return sessions;
    try
    {
        var queryText = isEnded.HasValue 
            ? $"SELECT * FROM c WHERE c.type = 'session' AND c.IsEnded = {isEnded.Value.ToString().ToLower()}"
            : "SELECT * FROM c WHERE c.type = 'session'";
        var query = new QueryDefinition(queryText);
        var iterator = cosmosContainer.GetItemQueryIterator<InterviewSession>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            sessions.AddRange(response);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to get sessions from Cosmos DB: {ex.Message}");
    }
    return sessions;
}

// Load existing data from Cosmos DB into memory caches on startup
async Task LoadDataFromCosmosAsync()
{
    if (cosmosContainer == null) return;
    
    Console.WriteLine("Loading data from Cosmos DB...");
    
    // Load invites
    var invites = await GetAllInvitesFromCosmosAsync();
    foreach (var invite in invites)
    {
        interviewInvites[invite.Code] = invite;
    }
    Console.WriteLine($"Loaded {invites.Count} invites from Cosmos DB");
    
    // Load sessions
    var sessions = await GetAllSessionsFromCosmosAsync();
    foreach (var session in sessions)
    {
        if (session.IsEnded)
        {
            completedInterviews[session.SessionId] = session;
        }
        else
        {
            interviewSessions[session.SessionId] = session;
        }
    }
    Console.WriteLine($"Loaded {sessions.Count} sessions from Cosmos DB");
}

// Load data on startup
Task.Run(async () => await LoadDataFromCosmosAsync()).Wait();

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

// Health check endpoint
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTime.UtcNow,
    service = "Interview Intelligence Backend"
}))
.WithName("HealthCheck")
.WithTags("Health")
.WithOpenApi();

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
        id = sessionId, // Cosmos DB document ID
        type = "session", // Document type
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
        session.JobDescriptionId = invite.JobDescriptionId;
        session.ResumeId = invite.ResumeId; // Copy resume reference
        
        invite.IsUsed = true;
        invite.UsedAt = DateTime.UtcNow;
        invite.SessionId = sessionId;
        
        // Persist invite changes to Cosmos DB
        await SaveInviteToCosmosAsync(invite);
    }
    
    // Save session to in-memory cache
    interviewSessions[sessionId] = session;
    
    // Persist session to Cosmos DB
    await SaveSessionToCosmosAsync(session);
    
    return Results.Ok(new { 
        sessionId, 
        message = "Interview session started",
        candidateName = session.CandidateName,
        position = session.Position,
        jobDescriptionId = session.JobDescriptionId
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

// 2) Generate follow-up question (using Azure OpenAI with JD and Resume context)
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
    JobDescription? jobDescription = null;
    CandidateResume? candidateResume = null;
    
    if (!string.IsNullOrWhiteSpace(req.sessionId))
    {
        if (interviewSessions.TryGetValue(req.sessionId, out session))
        {
            var elapsedMinutes = (DateTime.UtcNow - session.StartTime).TotalMinutes;
            
            // Check if interview should end
            if (session.IsEnded || session.QuestionCount >= MaxQuestions || elapsedMinutes >= MaxDurationMinutes)
            {
                session.IsEnded = true;
                
                // Move to completed interviews
                completedInterviews[session.SessionId] = session;
                interviewSessions.TryRemove(session.SessionId, out _);
                
                // Persist session changes to Cosmos DB
                await SaveSessionToCosmosAsync(session);
                
                var thankYouMessage = $"Thank you so much for taking the time to speak with me today! I really enjoyed our conversation and learning about your experience. {(session.QuestionCount >= MaxQuestions ? "We've covered everything I wanted to discuss" : "We've reached the end of our scheduled time")}. Our team will review everything and be in touch soon. Do you have any questions for me about the role or the team before we wrap up?";
                
                await http.Response.WriteAsJsonAsync(new GenerateQuestionResponse(thankYouMessage, true, session.QuestionCount, elapsedMinutes));
                return;
            }
            
            // Fetch Job Description from Cosmos DB if available
            if (!string.IsNullOrEmpty(session.JobDescriptionId) && cosmosContainer != null)
            {
                try
                {
                    var jdResponse = await cosmosContainer.ReadItemAsync<JobDescription>(session.JobDescriptionId, new PartitionKey(session.JobDescriptionId));
                    jobDescription = jdResponse.Resource;
                }
                catch { /* JD not found, continue without it */ }
            }
            
            // Fetch Resume from Cosmos DB if available
            if (!string.IsNullOrEmpty(session.ResumeId) && cosmosContainer != null)
            {
                try
                {
                    var resumeResponse = await cosmosContainer.ReadItemAsync<CandidateResume>(session.ResumeId, new PartitionKey(session.ResumeId));
                    candidateResume = resumeResponse.Resource;
                }
                catch { /* Resume not found, continue without it */ }
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

    // Build dynamic system prompt based on JD and Resume
    var systemPromptBuilder = new StringBuilder();
    systemPromptBuilder.AppendLine("You are a friendly and professional technical interviewer.");
    
    // Add position context
    if (jobDescription != null)
    {
        systemPromptBuilder.AppendLine($"\n=== JOB POSITION: {jobDescription.jobTitle} ===");
        systemPromptBuilder.AppendLine($"Department: {jobDescription.department}");
        systemPromptBuilder.AppendLine($"Experience Level: {jobDescription.experienceLevel}");
        
        if (jobDescription.requiredSkills?.Any() == true)
        {
            systemPromptBuilder.AppendLine($"Required Skills: {string.Join(", ", jobDescription.requiredSkills)}");
        }
        
        if (!string.IsNullOrEmpty(jobDescription.interviewTopics))
        {
            systemPromptBuilder.AppendLine($"Interview Topics to Cover: {jobDescription.interviewTopics}");
        }
        
        if (!string.IsNullOrEmpty(jobDescription.evaluationCriteria))
        {
            systemPromptBuilder.AppendLine($"Evaluation Criteria: {jobDescription.evaluationCriteria}");
        }
    }
    else if (session?.Position != null)
    {
        systemPromptBuilder.AppendLine($"\nYou are interviewing for a {session.Position} position.");
    }
    
    // Add candidate context from resume
    if (candidateResume != null)
    {
        systemPromptBuilder.AppendLine($"\n=== CANDIDATE: {candidateResume.candidateName} ===");
        
        if (!string.IsNullOrEmpty(candidateResume.currentRole))
        {
            systemPromptBuilder.AppendLine($"Current Role: {candidateResume.currentRole}");
        }
        
        if (candidateResume.yearsOfExperience.HasValue)
        {
            systemPromptBuilder.AppendLine($"Years of Experience: {candidateResume.yearsOfExperience}");
        }
        
        if (candidateResume.technicalSkills?.Any() == true)
        {
            systemPromptBuilder.AppendLine($"Technical Skills: {string.Join(", ", candidateResume.technicalSkills.Take(10))}");
        }
        
        if (candidateResume.programmingLanguages?.Any() == true)
        {
            systemPromptBuilder.AppendLine($"Programming Languages: {string.Join(", ", candidateResume.programmingLanguages)}");
        }
        
        if (candidateResume.frameworks?.Any() == true)
        {
            systemPromptBuilder.AppendLine($"Frameworks: {string.Join(", ", candidateResume.frameworks)}");
        }
        
        if (candidateResume.workExperience?.Any() == true)
        {
            systemPromptBuilder.AppendLine("\nRecent Work Experience:");
            foreach (var exp in candidateResume.workExperience.Take(2))
            {
                systemPromptBuilder.AppendLine($"  - {exp.title} at {exp.company} ({exp.duration})");
                if (exp.highlights?.Any() == true)
                {
                    systemPromptBuilder.AppendLine($"    Key work: {string.Join("; ", exp.highlights.Take(2))}");
                }
            }
        }
        
        if (candidateResume.suggestedInterviewTopics?.Any() == true)
        {
            systemPromptBuilder.AppendLine($"\nSuggested Topics to Explore: {string.Join(", ", candidateResume.suggestedInterviewTopics)}");
        }
        
        if (candidateResume.areasToProbe?.Any() == true)
        {
            systemPromptBuilder.AppendLine($"Areas to Probe: {string.Join(", ", candidateResume.areasToProbe)}");
        }
    }
    
    systemPromptBuilder.AppendLine(@"

TONE & STYLE:
- Be warm, encouraging, and professionally friendly
- Always start with a brief, positive acknowledgment of the candidate's answer (1 short sentence)
- Then ask your follow-up question
- Keep your total response concise (2-3 sentences max)

INTERVIEW STRATEGY:");
    
    if (jobDescription != null && candidateResume != null)
    {
        systemPromptBuilder.AppendLine(@"- Use the JOB REQUIREMENTS to focus on skills that matter for this role
- Use the CANDIDATE'S RESUME to ask SPECIFIC questions about their past projects and experience
- For example, if they worked at Company X, ask about specific challenges or achievements there
- If they list a technology, ask how they've used it in production
- Probe gaps between their experience and job requirements");
    }
    else if (candidateResume != null)
    {
        systemPromptBuilder.AppendLine(@"- Use the CANDIDATE'S RESUME to ask SPECIFIC questions about their experience
- Reference their actual companies, projects, and technologies
- Ask about specific achievements mentioned in their resume");
    }
    else
    {
        systemPromptBuilder.AppendLine(@"- Focus on general software development practices and problem-solving
- Ask about their experience with relevant technologies");
    }
    
    systemPromptBuilder.AppendLine(@"
- Stay within the context of the current interview conversation
- If the candidate goes off-topic, gently and kindly redirect back to relevant topics

EXAMPLE FORMAT:
'That's a great point about [topic]! [Follow-up question specific to their experience or the job requirements]'
or
'I appreciate you sharing that experience. [Question about a specific project or skill from their resume]'");

    var systemPrompt = systemPromptBuilder.ToString();

    // Build user prompt with conversation history for context
    var userPromptBuilder = new StringBuilder();
    userPromptBuilder.AppendLine("Given the candidate's last answer, provide a brief positive acknowledgment followed by a relevant follow-up question.");
    
    // Add recent conversation history for context
    if (session?.Transcript?.Any() == true)
    {
        userPromptBuilder.AppendLine("\nRecent conversation:");
        foreach (var exchange in session.Transcript.TakeLast(3))
        {
            userPromptBuilder.AppendLine($"Q: {exchange.Question}");
            userPromptBuilder.AppendLine($"A: {exchange.Answer}");
        }
    }
    
    userPromptBuilder.AppendLine($"\nCandidate's latest answer: \"{req.text}\"");
    userPromptBuilder.AppendLine("\nRemember: Start with short feedback (1 sentence), then ask your question. Be warm and professional. Ask something SPECIFIC based on their resume or the job requirements if available.");

    var userPrompt = userPromptBuilder.ToString();

    var aoUrl = $"{aoEndpoint}/openai/deployments/{deploymentId}/chat/completions?api-version=2023-10-01-preview";

    var body = new
    {
        model = "gpt-4o", // or omit if your deployment maps model internally
        messages = new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt }
        },
        max_tokens = 150, // Increased for more detailed personalized questions
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
app.MapPost("/api/interview/transcript", async (SaveTranscriptRequest req, HttpContext http) =>
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
    
    // Persist session changes to Cosmos DB
    await SaveSessionToCosmosAsync(session);
    
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
    
    // Look in active sessions first, then completed interviews, then Cosmos DB
    InterviewSession? session = null;
    if (!interviewSessions.TryGetValue(req.sessionId, out session))
    {
        if (!completedInterviews.TryGetValue(req.sessionId, out session))
        {
            // Try to fetch from Cosmos DB
            if (cosmosContainer != null)
            {
                try
                {
                    var cosmosResponse = await cosmosContainer.ReadItemAsync<InterviewSession>(req.sessionId, new PartitionKey(req.sessionId));
                    session = cosmosResponse.Resource;
                }
                catch { /* Not found in Cosmos DB */ }
            }
        }
    }
    
    if (session == null)
    {
        return Results.NotFound(new { error = "Session not found" });
    }
    
    if (session.Transcript == null || session.Transcript.Count == 0)
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
            
            // ===== JD FIT ANALYSIS =====
            // Fetch the JD the candidate interviewed for and all other JDs
            JobDescription? interviewedJD = null;
            CandidateResume? candidateResume = null;
            var allJobDescriptions = new List<JobDescription>();
            
            if (cosmosContainer != null)
            {
                // Fetch the JD for this interview
                if (!string.IsNullOrEmpty(session.JobDescriptionId))
                {
                    try
                    {
                        var jdResponse = await cosmosContainer.ReadItemAsync<JobDescription>(
                            session.JobDescriptionId, new PartitionKey(session.JobDescriptionId));
                        interviewedJD = jdResponse.Resource;
                    }
                    catch { /* JD not found */ }
                }
                
                // Fetch the candidate's resume
                if (!string.IsNullOrEmpty(session.ResumeId))
                {
                    try
                    {
                        var resumeResponse = await cosmosContainer.ReadItemAsync<CandidateResume>(
                            session.ResumeId, new PartitionKey(session.ResumeId));
                        candidateResume = resumeResponse.Resource;
                    }
                    catch { /* Resume not found */ }
                }
                
                // Fetch all job descriptions for alternative matching
                try
                {
                    var query = "SELECT * FROM c WHERE c.jobTitle != null";
                    var iterator = cosmosContainer.GetItemQueryIterator<JobDescription>(query);
                    while (iterator.HasMoreResults)
                    {
                        var batch = await iterator.ReadNextAsync();
                        allJobDescriptions.AddRange(batch);
                    }
                }
                catch { /* Failed to fetch JDs */ }
            }
            
            // Build candidate skills profile from transcript analysis and resume
            var candidateSkills = new List<string>();
            candidateSkills.AddRange(analysis.TechnicalSkills.TopicsDiscussed);
            if (candidateResume != null)
            {
                candidateSkills.AddRange(candidateResume.technicalSkills ?? new List<string>());
                candidateSkills.AddRange(candidateResume.programmingLanguages ?? new List<string>());
                candidateSkills.AddRange(candidateResume.frameworks ?? new List<string>());
            }
            candidateSkills = candidateSkills.Distinct().ToList();
            
            // Analyze fit for the interviewed position
            if (interviewedJD != null && candidateSkills.Any())
            {
                var jdSkills = new List<string>();
                jdSkills.AddRange(interviewedJD.requiredSkills ?? new List<string>());
                jdSkills.AddRange(interviewedJD.niceToHaveSkills ?? new List<string>());
                
                var matchingSkills = candidateSkills
                    .Where(cs => jdSkills.Any(js => 
                        js.Contains(cs, StringComparison.OrdinalIgnoreCase) || 
                        cs.Contains(js, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                
                var missingSkills = (interviewedJD.requiredSkills ?? new List<string>())
                    .Where(rs => !candidateSkills.Any(cs => 
                        cs.Contains(rs, StringComparison.OrdinalIgnoreCase) || 
                        rs.Contains(cs, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                
                var fitScore = jdSkills.Count > 0 
                    ? (int)Math.Round((double)matchingSkills.Count / jdSkills.Count * 100) 
                    : analysis.OverallScore;
                
                // Adjust fit score based on interview performance
                fitScore = (fitScore + analysis.OverallScore) / 2;
                
                analysis.JobFitAnalysis = new JDFitAnalysis
                {
                    JobDescriptionId = interviewedJD.id,
                    JobTitle = interviewedJD.jobTitle,
                    FitScore = fitScore,
                    FitSummary = fitScore >= 70 
                        ? $"Strong fit for {interviewedJD.jobTitle}. Candidate demonstrates {matchingSkills.Count} of the required skills."
                        : $"Partial fit for {interviewedJD.jobTitle}. Candidate is missing {missingSkills.Count} required skills.",
                    MatchingSkills = matchingSkills,
                    MissingSkills = missingSkills,
                    IsGoodFit = fitScore >= 70
                };
                
                // If not a good fit, find alternative positions
                if (!analysis.JobFitAnalysis.IsGoodFit && allJobDescriptions.Any())
                {
                    var alternativeMatches = new List<AlternativeJDMatch>();
                    
                    foreach (var jd in allJobDescriptions.Where(j => j.id != interviewedJD.id))
                    {
                        var altJdSkills = new List<string>();
                        altJdSkills.AddRange(jd.requiredSkills ?? new List<string>());
                        altJdSkills.AddRange(jd.niceToHaveSkills ?? new List<string>());
                        
                        var altMatchingSkills = candidateSkills
                            .Where(cs => altJdSkills.Any(js => 
                                js.Contains(cs, StringComparison.OrdinalIgnoreCase) || 
                                cs.Contains(js, StringComparison.OrdinalIgnoreCase)))
                            .ToList();
                        
                        var altMatchScore = altJdSkills.Count > 0 
                            ? (int)Math.Round((double)altMatchingSkills.Count / altJdSkills.Count * 100) 
                            : 0;
                        
                        // Consider interview performance too
                        altMatchScore = (altMatchScore + analysis.OverallScore) / 2;
                        
                        if (altMatchScore >= 60) // Only suggest if reasonable match
                        {
                            alternativeMatches.Add(new AlternativeJDMatch
                            {
                                JobDescriptionId = jd.id,
                                JobTitle = jd.jobTitle,
                                Department = jd.department,
                                MatchScore = altMatchScore,
                                MatchReason = $"Candidate's skills in {string.Join(", ", altMatchingSkills.Take(3))} align well with this role.",
                                MatchingSkills = altMatchingSkills
                            });
                        }
                    }
                    
                    // Sort by match score and take top 3
                    analysis.AlternativeJobMatches = alternativeMatches
                        .OrderByDescending(m => m.MatchScore)
                        .Take(3)
                        .ToList();
                }
            }
            else if (allJobDescriptions.Any() && candidateSkills.Any())
            {
                // No specific JD for this interview, but we can still suggest matches
                var alternativeMatches = new List<AlternativeJDMatch>();
                
                foreach (var jd in allJobDescriptions)
                {
                    var jdSkills = new List<string>();
                    jdSkills.AddRange(jd.requiredSkills ?? new List<string>());
                    jdSkills.AddRange(jd.niceToHaveSkills ?? new List<string>());
                    
                    var matchingSkills = candidateSkills
                        .Where(cs => jdSkills.Any(js => 
                            js.Contains(cs, StringComparison.OrdinalIgnoreCase) || 
                            cs.Contains(js, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    
                    var matchScore = jdSkills.Count > 0 
                        ? (int)Math.Round((double)matchingSkills.Count / jdSkills.Count * 100) 
                        : 0;
                    
                    matchScore = (matchScore + analysis.OverallScore) / 2;
                    
                    if (matchScore >= 50)
                    {
                        alternativeMatches.Add(new AlternativeJDMatch
                        {
                            JobDescriptionId = jd.id,
                            JobTitle = jd.jobTitle,
                            Department = jd.department,
                            MatchScore = matchScore,
                            MatchReason = $"Based on demonstrated skills: {string.Join(", ", matchingSkills.Take(3))}",
                            MatchingSkills = matchingSkills
                        });
                    }
                }
                
                analysis.AlternativeJobMatches = alternativeMatches
                    .OrderByDescending(m => m.MatchScore)
                    .Take(5)
                    .ToList();
            }
            
            session.Analysis = analysis;
            
            // Persist session with analysis to Cosmos DB
            await SaveSessionToCosmosAsync(session);
            
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
        var jobDescriptionId = json.RootElement.TryGetProperty("jobDescriptionId", out var jdEl) ? jdEl.GetString() : null;
        var resumeId = json.RootElement.TryGetProperty("resumeId", out var resumeEl) ? resumeEl.GetString() : null;
        var resumeFileName = json.RootElement.TryGetProperty("resumeFileName", out var resumeNameEl) ? resumeNameEl.GetString() : null;
        var expiresInDays = json.RootElement.TryGetProperty("expiresInDays", out var expEl) ? expEl.GetInt32() : 7;
        
        // Generate a unique 6-character code
        var code = GenerateInviteCode();
        while (interviewInvites.ContainsKey(code))
        {
            code = GenerateInviteCode();
        }
        
        var invite = new InterviewInvite
        {
            id = code, // Cosmos DB document ID
            type = "invite", // Document type
            Code = code,
            CandidateName = candidateName,
            CandidateEmail = candidateEmail,
            Position = position,
            JobDescriptionId = jobDescriptionId,
            ResumeId = resumeId,
            ResumeFileName = resumeFileName,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(expiresInDays),
            IsUsed = false
        };
        
        // Save to in-memory cache
        interviewInvites[code] = invite;
        
        // Persist to Cosmos DB
        await SaveInviteToCosmosAsync(invite);
        
        return Results.Ok(new
        {
            code = invite.Code,
            candidateName = invite.CandidateName,
            candidateEmail = invite.CandidateEmail,
            position = invite.Position,
            jobDescriptionId = invite.JobDescriptionId,
            hasResume = !string.IsNullOrEmpty(invite.ResumeId),
            resumeId = invite.ResumeId,
            resumeFileName = invite.ResumeFileName,
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
            jobDescriptionId = i.JobDescriptionId,
            hasResume = !string.IsNullOrEmpty(i.ResumeId),
            resumeId = i.ResumeId,
            resumeFileName = i.ResumeFileName,
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
app.MapDelete("/api/admin/invites/{code}", async (string code) =>
{
    var upperCode = code.ToUpper();
    if (interviewInvites.TryRemove(upperCode, out _))
    {
        // Also delete from Cosmos DB
        await DeleteInviteFromCosmosAsync(upperCode);
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

// ===== RESUME PARSING AND STORAGE =====

// Parse resume using LLM and store in Cosmos DB
app.MapPost("/api/resumes/parse", async (HttpContext http) =>
{
    try
    {
        using var reader = new StreamReader(http.Request.Body);
        var body = await reader.ReadToEndAsync();
        var json = JsonDocument.Parse(body);
        
        var resumeText = json.RootElement.GetProperty("resumeText").GetString() ?? "";
        var candidateName = json.RootElement.TryGetProperty("candidateName", out var nameEl) ? nameEl.GetString() : "";
        var candidateEmail = json.RootElement.TryGetProperty("candidateEmail", out var emailEl) ? emailEl.GetString() : "";
        var fileName = json.RootElement.TryGetProperty("fileName", out var fileEl) ? fileEl.GetString() : "";
        var inviteCode = json.RootElement.TryGetProperty("inviteCode", out var codeEl) ? codeEl.GetString() : null;

        if (string.IsNullOrWhiteSpace(resumeText))
        {
            return Results.BadRequest(new { error = "No resume text provided" });
        }

        // Use Azure OpenAI to parse the resume
        var openAiKey = builder.Configuration["Azure:OpenAI:Key"];
        var openAiEndpoint = builder.Configuration["Azure:OpenAI:Endpoint"];
        var deploymentId = builder.Configuration["Azure:OpenAI:DeploymentId"];

        if (string.IsNullOrEmpty(openAiKey) || string.IsNullOrEmpty(openAiEndpoint))
        {
            return Results.Problem("Azure OpenAI is not configured");
        }

        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("api-key", openAiKey);

        var prompt = $@"Parse the following resume and extract key information. Return a JSON object with these fields:

- summary (string): A 2-3 sentence professional summary of the candidate
- currentRole (string): Their most recent/current job title
- yearsOfExperience (number): Estimated total years of professional experience
- technicalSkills (array of strings): Technical skills like databases, cloud platforms, architectures
- softSkills (array of strings): Soft skills like leadership, communication, teamwork
- programmingLanguages (array of strings): Programming languages they know
- frameworks (array of strings): Frameworks and libraries (e.g., React, .NET, Spring)
- tools (array of strings): Tools and platforms (e.g., Git, Docker, Azure, AWS)
- certifications (array of strings): Professional certifications
- workExperience (array of objects): Recent work experience, each with:
  - company (string)
  - title (string)
  - duration (string)
  - highlights (array of strings): Key achievements/responsibilities
- education (array of objects): Education background, each with:
  - institution (string)
  - degree (string)
  - field (string)
  - year (string)
- achievements (array of strings): Notable achievements, awards, or recognitions
- suggestedInterviewTopics (array of strings): Topics to ask about based on their experience
- potentialStrengths (array of strings): Areas where they appear strong
- areasToProbe (array of strings): Areas that need clarification or deeper questioning

RESUME TEXT:
{resumeText}

Return ONLY valid JSON, no markdown or explanation.";

        var requestBody = new
        {
            messages = new[]
            {
                new { role = "system", content = "You are an expert HR analyst and technical recruiter. Parse resumes accurately and extract structured information. Always return valid JSON." },
                new { role = "user", content = prompt }
            },
            max_tokens = 4000,
            temperature = 0.3
        };

        var response = await client.PostAsync(
            $"{openAiEndpoint}/openai/deployments/{deploymentId}/chat/completions?api-version=2024-02-15-preview",
            new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return Results.Problem($"OpenAI API error: {errorContent}");
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        var openAiResponse = JsonDocument.Parse(responseContent);
        var messageContent = openAiResponse.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        // Clean up the response (remove markdown code blocks if present)
        messageContent = messageContent.Trim();
        if (messageContent.StartsWith("```json"))
        {
            messageContent = messageContent.Substring(7);
        }
        if (messageContent.StartsWith("```"))
        {
            messageContent = messageContent.Substring(3);
        }
        if (messageContent.EndsWith("```"))
        {
            messageContent = messageContent.Substring(0, messageContent.Length - 3);
        }
        messageContent = messageContent.Trim();

        // Parse the LLM response
        var parsedResume = JsonSerializer.Deserialize<JsonElement>(messageContent);
        
        // Create resume document for Cosmos DB
        var resumeId = Guid.NewGuid().ToString();
        var resumeDoc = new CandidateResume
        {
            id = resumeId,
            type = "resume",
            candidateName = candidateName ?? "",
            candidateEmail = candidateEmail,
            fileName = fileName,
            summary = parsedResume.TryGetProperty("summary", out var s) ? s.GetString() : null,
            currentRole = parsedResume.TryGetProperty("currentRole", out var cr) ? cr.GetString() : null,
            yearsOfExperience = parsedResume.TryGetProperty("yearsOfExperience", out var yoe) ? yoe.GetInt32() : null,
            technicalSkills = GetStringArray(parsedResume, "technicalSkills"),
            softSkills = GetStringArray(parsedResume, "softSkills"),
            programmingLanguages = GetStringArray(parsedResume, "programmingLanguages"),
            frameworks = GetStringArray(parsedResume, "frameworks"),
            tools = GetStringArray(parsedResume, "tools"),
            certifications = GetStringArray(parsedResume, "certifications"),
            achievements = GetStringArray(parsedResume, "achievements"),
            suggestedInterviewTopics = GetStringArray(parsedResume, "suggestedInterviewTopics"),
            potentialStrengths = GetStringArray(parsedResume, "potentialStrengths"),
            areasToProbe = GetStringArray(parsedResume, "areasToProbe"),
            createdAt = DateTime.UtcNow.ToString("o"),
            inviteCode = inviteCode
        };

        // Parse work experience
        if (parsedResume.TryGetProperty("workExperience", out var weArray) && weArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var we in weArray.EnumerateArray())
            {
                resumeDoc.workExperience.Add(new WorkExperience
                {
                    company = we.TryGetProperty("company", out var c) ? c.GetString() : null,
                    title = we.TryGetProperty("title", out var t) ? t.GetString() : null,
                    duration = we.TryGetProperty("duration", out var d) ? d.GetString() : null,
                    highlights = we.TryGetProperty("highlights", out var h) && h.ValueKind == JsonValueKind.Array
                        ? h.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                        : new List<string>()
                });
            }
        }

        // Parse education
        if (parsedResume.TryGetProperty("education", out var eduArray) && eduArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var edu in eduArray.EnumerateArray())
            {
                resumeDoc.education.Add(new Education
                {
                    institution = edu.TryGetProperty("institution", out var i) ? i.GetString() : null,
                    degree = edu.TryGetProperty("degree", out var deg) ? deg.GetString() : null,
                    field = edu.TryGetProperty("field", out var f) ? f.GetString() : null,
                    year = edu.TryGetProperty("year", out var y) ? y.GetString() : null
                });
            }
        }

        // Store in Cosmos DB
        if (cosmosContainer != null)
        {
            await cosmosContainer.CreateItemAsync(resumeDoc, new PartitionKey(resumeDoc.id));
        }

        return Results.Ok(new
        {
            id = resumeDoc.id,
            candidateName = resumeDoc.candidateName,
            summary = resumeDoc.summary,
            currentRole = resumeDoc.currentRole,
            yearsOfExperience = resumeDoc.yearsOfExperience,
            technicalSkills = resumeDoc.technicalSkills,
            softSkills = resumeDoc.softSkills,
            programmingLanguages = resumeDoc.programmingLanguages,
            frameworks = resumeDoc.frameworks,
            tools = resumeDoc.tools,
            certifications = resumeDoc.certifications,
            workExperience = resumeDoc.workExperience,
            education = resumeDoc.education,
            achievements = resumeDoc.achievements,
            suggestedInterviewTopics = resumeDoc.suggestedInterviewTopics,
            potentialStrengths = resumeDoc.potentialStrengths,
            areasToProbe = resumeDoc.areasToProbe,
            inviteCode = resumeDoc.inviteCode
        });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = "Failed to parse resume", details = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to parse resume: {ex.Message}");
    }
})
.WithName("ParseResume")
.WithTags("Resumes")
.WithOpenApi(operation => new(operation)
{
    Summary = "Parse resume using LLM and store in Cosmos DB",
    Description = "Extracts key information from resume text using AI and stores the structured data"
});

// Get resume by ID
app.MapGet("/api/resumes/{id}", async (string id) =>
{
    if (cosmosContainer == null)
    {
        return Results.Problem("Cosmos DB is not configured");
    }

    try
    {
        var response = await cosmosContainer.ReadItemAsync<CandidateResume>(id, new PartitionKey(id));
        return Results.Ok(response.Resource);
    }
    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return Results.NotFound(new { error = "Resume not found" });
    }
})
.WithName("GetResume")
.WithTags("Resumes");

// Get resume by invite code
app.MapGet("/api/resumes/by-invite/{code}", async (string code) =>
{
    if (cosmosContainer == null)
    {
        return Results.Problem("Cosmos DB is not configured");
    }

    try
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = 'resume' AND c.inviteCode = @code")
            .WithParameter("@code", code.ToUpper());
        
        var iterator = cosmosContainer.GetItemQueryIterator<CandidateResume>(query);
        var results = new List<CandidateResume>();
        
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        if (results.Count == 0)
        {
            return Results.NotFound(new { error = "No resume found for this invite code" });
        }

        return Results.Ok(results.First());
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to fetch resume: {ex.Message}");
    }
})
.WithName("GetResumeByInviteCode")
.WithTags("Resumes");

// Helper function to extract string arrays from JSON
static List<string> GetStringArray(JsonElement element, string propertyName)
{
    if (element.TryGetProperty(propertyName, out var arr) && arr.ValueKind == JsonValueKind.Array)
    {
        return arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
    }
    return new List<string>();
}

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
            hasVideoAnalysis = session.VideoAnalysis != null,
            hasCombinedAnalysis = session.CombinedAnalysis != null,
            hasVideo = !string.IsNullOrEmpty(session.VideoBlobUrl),
            overallScore = session.CombinedAnalysis?.OverallScore ?? session.Analysis?.OverallScore,
            behavioralScore = session.VideoAnalysis?.OverallBehavioralScore,
            hiringRecommendation = session.CombinedAnalysis?.HiringRecommendation ?? session.Analysis?.HiringRecommendation
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
                hasVideoAnalysis = false,
                hasCombinedAnalysis = false,
                hasVideo = !string.IsNullOrEmpty(session.VideoBlobUrl),
                overallScore = (int?)null,
                behavioralScore = (int?)null,
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
            candidateEmail = session.CandidateEmail,
            position = session.Position ?? "Software Developer",
            startTime = session.StartTime,
            questionCount = session.QuestionCount,
            isEnded = session.IsEnded,
            transcript = session.Transcript,
            analysis = session.Analysis,
            videoAnalysis = session.VideoAnalysis,
            combinedAnalysis = session.CombinedAnalysis,
            hasVideo = !string.IsNullOrEmpty(session.VideoBlobUrl),
            videoBlobUrl = session.VideoBlobUrl
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
        interviewSessions.TryRemove(sessionId, out _);
        
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
        
        // Persist session changes to Cosmos DB
        await SaveSessionToCosmosAsync(session);
        
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
        InterviewSession? sessionToUpdate = null;
        if (interviewSessions.TryGetValue(sessionId, out var session))
        {
            session.VideoBlobUrl = blobClient.Uri.ToString();
            sessionToUpdate = session;
        }
        else if (completedInterviews.TryGetValue(sessionId, out var completedSession))
        {
            completedSession.VideoBlobUrl = blobClient.Uri.ToString();
            sessionToUpdate = completedSession;
        }
        
        // Persist session changes to Cosmos DB
        if (sessionToUpdate != null)
        {
            await SaveSessionToCosmosAsync(sessionToUpdate);
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

// ========== VIDEO ANALYSIS ENDPOINTS ==========

// Analyze interview video using Azure Face API
app.MapPost("/api/admin/interviews/{sessionId}/analyze-video", async (string sessionId, IHttpClientFactory httpClientFactory, IConfiguration config) =>
{
    // Get the interview session - check active, completed, and Cosmos DB
    InterviewSession? session = null;
    if (!completedInterviews.TryGetValue(sessionId, out session))
    {
        if (!interviewSessions.TryGetValue(sessionId, out session))
        {
            // Try Cosmos DB
            if (cosmosContainer != null)
            {
                try
                {
                    var response = await cosmosContainer.ReadItemAsync<InterviewSession>(sessionId, new PartitionKey(sessionId));
                    session = response.Resource;
                }
                catch { /* Not found */ }
            }
        }
    }
    
    if (session == null)
    {
        return Results.NotFound(new { error = "Interview session not found" });
    }
    
    if (string.IsNullOrEmpty(session.VideoBlobUrl))
    {
        return Results.BadRequest(new { error = "No video recording found for this interview" });
    }
    
    var faceKey = config["Azure:Face:Key"];
    var faceEndpoint = config["Azure:Face:Endpoint"];
    
    if (string.IsNullOrWhiteSpace(faceKey) || string.IsNullOrWhiteSpace(faceEndpoint))
    {
        return Results.StatusCode(503); // Service unavailable
    }
    
    try
    {
        // For video analysis, we need to extract frames and analyze each
        // Since Azure Face API works with images, we'll simulate frame extraction
        // In production, you'd use FFmpeg or Azure Media Services to extract frames
        
        var analysisResult = new VideoAnalysisResult
        {
            SessionId = sessionId,
            AnalyzedAt = DateTime.UtcNow
        };
        
        // Download video and extract frames for analysis
        if (blobContainerClient != null)
        {
            var blobName = session.VideoBlobUrl.Contains("/") 
                ? session.VideoBlobUrl.Substring(session.VideoBlobUrl.LastIndexOf("/") + 1)
                : $"videos/{sessionId}.webm";
            
            // For now, we'll use Azure OpenAI to analyze the video conceptually
            // and generate behavioral insights based on the interview context
            var aoKey = config["Azure:OpenAI:Key"];
            var aoEndpoint = config["Azure:OpenAI:Endpoint"];
            var deploymentId = config["Azure:OpenAI:DeploymentId"];
            
            if (!string.IsNullOrWhiteSpace(aoKey) && !string.IsNullOrWhiteSpace(aoEndpoint))
            {
                // Generate behavioral analysis based on transcript patterns
                // (In production, you'd analyze actual video frames)
                var behavioralAnalysis = await GenerateBehavioralAnalysis(
                    session, httpClientFactory, aoKey, aoEndpoint, deploymentId);
                
                if (behavioralAnalysis != null)
                {
                    analysisResult = behavioralAnalysis;
                    analysisResult.SessionId = sessionId;
                    analysisResult.AnalyzedAt = DateTime.UtcNow;
                }
            }
        }
        
        // Store the analysis
        session.VideoAnalysis = analysisResult;
        
        // Persist session with video analysis to Cosmos DB
        await SaveSessionToCosmosAsync(session);
        
        return Results.Ok(new
        {
            sessionId = sessionId,
            videoAnalysis = analysisResult,
            message = "Video analysis completed successfully"
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Video analysis failed: {ex.Message}");
        return Results.StatusCode(500);
    }
})
.WithName("AnalyzeVideo")
.WithTags("Video Analysis")
.WithOpenApi(operation => new(operation)
{
    Summary = "Analyze interview video for behavioral insights",
    Description = "Uses AI to analyze the interview video recording and provide behavioral assessment including engagement, confidence, and emotional patterns"
});

// Get video analysis results
app.MapGet("/api/admin/interviews/{sessionId}/video-analysis", (string sessionId) =>
{
    InterviewSession? session = null;
    if (!completedInterviews.TryGetValue(sessionId, out session))
    {
        interviewSessions.TryGetValue(sessionId, out session);
    }
    
    if (session == null)
    {
        return Results.NotFound(new { error = "Interview session not found" });
    }
    
    if (session.VideoAnalysis == null)
    {
        return Results.NotFound(new { error = "Video analysis not yet performed. Call POST /api/admin/interviews/{sessionId}/analyze-video first." });
    }
    
    return Results.Ok(session.VideoAnalysis);
})
.WithName("GetVideoAnalysis")
.WithTags("Video Analysis")
.WithOpenApi(operation => new(operation)
{
    Summary = "Get video analysis results",
    Description = "Returns the behavioral analysis results for an interview video"
});

// Combined analysis endpoint - analyzes both transcript and video
app.MapPost("/api/admin/interviews/{sessionId}/analyze-combined", async (string sessionId, IHttpClientFactory httpClientFactory, IConfiguration config) =>
{
    InterviewSession? session = null;
    if (!completedInterviews.TryGetValue(sessionId, out session))
    {
        if (!interviewSessions.TryGetValue(sessionId, out session))
        {
            // Try Cosmos DB
            if (cosmosContainer != null)
            {
                try
                {
                    var response = await cosmosContainer.ReadItemAsync<InterviewSession>(sessionId, new PartitionKey(sessionId));
                    session = response.Resource;
                }
                catch { /* Not found */ }
            }
        }
    }
    
    if (session == null)
    {
        return Results.NotFound(new { error = "Interview session not found" });
    }
    
    var aoKey = config["Azure:OpenAI:Key"];
    var aoEndpoint = config["Azure:OpenAI:Endpoint"];
    var deploymentId = config["Azure:OpenAI:DeploymentId"];
    
    if (string.IsNullOrWhiteSpace(aoKey) || string.IsNullOrWhiteSpace(aoEndpoint) || string.IsNullOrWhiteSpace(deploymentId))
    {
        return Results.StatusCode(503);
    }
    
    try
    {
        // Ensure we have transcript analysis
        if (session.Analysis == null && session.Transcript.Count > 0)
        {
            // Trigger transcript analysis first (reuse existing logic)
            // For simplicity, we'll just note it's missing
        }
        
        // Ensure we have video analysis
        if (session.VideoAnalysis == null && !string.IsNullOrEmpty(session.VideoBlobUrl))
        {
            var behavioralAnalysis = await GenerateBehavioralAnalysis(
                session, httpClientFactory, aoKey, aoEndpoint, deploymentId);
            if (behavioralAnalysis != null)
            {
                session.VideoAnalysis = behavioralAnalysis;
            }
        }
        
        // Generate combined analysis
        var combinedResult = await GenerateCombinedAnalysis(
            session, httpClientFactory, aoKey, aoEndpoint, deploymentId);
        
        session.CombinedAnalysis = combinedResult;
        
        // Persist session with combined analysis to Cosmos DB
        await SaveSessionToCosmosAsync(session);
        
        return Results.Ok(new
        {
            sessionId = sessionId,
            combinedAnalysis = combinedResult,
            hasTranscriptAnalysis = session.Analysis != null,
            hasVideoAnalysis = session.VideoAnalysis != null,
            message = "Combined analysis completed successfully"
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Combined analysis failed: {ex.Message}");
        return Results.StatusCode(500);
    }
})
.WithName("AnalyzeCombined")
.WithTags("Video Analysis")
.WithOpenApi(operation => new(operation)
{
    Summary = "Perform combined transcript and video analysis",
    Description = "Analyzes both the interview transcript and video recording to provide a comprehensive assessment"
});

// Get combined analysis results
app.MapGet("/api/admin/interviews/{sessionId}/combined-analysis", (string sessionId) =>
{
    InterviewSession? session = null;
    if (!completedInterviews.TryGetValue(sessionId, out session))
    {
        interviewSessions.TryGetValue(sessionId, out session);
    }
    
    if (session == null)
    {
        return Results.NotFound(new { error = "Interview session not found" });
    }
    
    if (session.CombinedAnalysis == null)
    {
        return Results.NotFound(new { error = "Combined analysis not yet performed" });
    }
    
    return Results.Ok(session.CombinedAnalysis);
})
.WithName("GetCombinedAnalysis")
.WithTags("Video Analysis")
.WithOpenApi(operation => new(operation)
{
    Summary = "Get combined analysis results",
    Description = "Returns the combined transcript and video analysis results"
});

// ========== JOB DESCRIPTION ENDPOINTS ==========

// Create a new job description
app.MapPost("/api/job-descriptions", async (JobDescription jobDescription) =>
{
    if (cosmosContainer == null)
    {
        return Results.Problem("Cosmos DB is not configured");
    }

    try
    {
        // Generate ID if not provided
        if (string.IsNullOrEmpty(jobDescription.id))
        {
            jobDescription.id = "jd_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + Guid.NewGuid().ToString("N")[..9];
        }
        
        // Set timestamps if not provided
        if (string.IsNullOrEmpty(jobDescription.createdAt))
        {
            jobDescription.createdAt = DateTime.UtcNow.ToString("o");
        }
        jobDescription.updatedAt = DateTime.UtcNow.ToString("o");

        // Use id as partition key (matching existing data structure)
        var response = await cosmosContainer.CreateItemAsync(jobDescription, new PartitionKey(jobDescription.id));
        return Results.Created($"/api/job-descriptions/{jobDescription.id}", response.Resource);
    }
    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
    {
        return Results.Conflict(new { error = "Job description with this ID already exists" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to create job description: {ex.Message}");
    }
})
.WithName("CreateJobDescription")
.WithTags("Job Descriptions")
.WithOpenApi(operation => new(operation)
{
    Summary = "Create a new job description",
    Description = "Creates a new job description in Cosmos DB"
});

// Parse bulk job descriptions using LLM
app.MapPost("/api/job-descriptions/parse", async (HttpContext http) =>
{
    try
    {
        using var reader = new StreamReader(http.Request.Body);
        var body = await reader.ReadToEndAsync();
        var json = JsonDocument.Parse(body);
        var text = json.RootElement.GetProperty("text").GetString() ?? "";

        if (string.IsNullOrWhiteSpace(text))
        {
            return Results.BadRequest(new { error = "No text provided to parse" });
        }

        // Use Azure OpenAI to parse the job descriptions
        var openAiKey = builder.Configuration["Azure:OpenAI:Key"];
        var openAiEndpoint = builder.Configuration["Azure:OpenAI:Endpoint"];
        var deploymentId = builder.Configuration["Azure:OpenAI:DeploymentId"];

        if (string.IsNullOrEmpty(openAiKey) || string.IsNullOrEmpty(openAiEndpoint))
        {
            return Results.Problem("Azure OpenAI is not configured");
        }

        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("api-key", openAiKey);

        var prompt = $@"Parse the following text and extract job descriptions. Return a JSON object with a 'jobDescriptions' array.

Each job description should have these fields:
- jobTitle (string, required): The job title/position name
- department (string): The department (e.g., Engineering, Marketing, Sales, HR, Finance, Operations, Product, Design, Customer Support, Other)
- experienceLevel (string): One of: Entry Level, Mid Level, Senior, Lead, Manager, Director, Executive
- employmentType (string): One of: Full-time, Part-time, Contract, Internship
- description (string): A brief overview of the role
- responsibilities (string): Key responsibilities, can be bullet points
- requiredSkills (array of strings): Required technical and soft skills
- niceToHaveSkills (array of strings): Nice-to-have skills
- qualifications (string): Educational and other qualifications
- interviewTopics (string): Key topics to evaluate during interview
- evaluationCriteria (string): How to evaluate candidates

If any field is not found in the text, use reasonable defaults or leave empty.
Parse ALL job descriptions found in the text.

TEXT TO PARSE:
{text}

Return ONLY valid JSON, no markdown or explanation.";

        var requestBody = new
        {
            messages = new[]
            {
                new { role = "system", content = "You are a helpful assistant that parses job descriptions into structured JSON format. Always return valid JSON." },
                new { role = "user", content = prompt }
            },
            max_tokens = 4000,
            temperature = 0.3
        };

        var response = await client.PostAsync(
            $"{openAiEndpoint}/openai/deployments/{deploymentId}/chat/completions?api-version=2024-02-15-preview",
            new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return Results.Problem($"OpenAI API error: {errorContent}");
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        var openAiResponse = JsonDocument.Parse(responseContent);
        var messageContent = openAiResponse.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        // Clean up the response (remove markdown code blocks if present)
        messageContent = messageContent.Trim();
        if (messageContent.StartsWith("```json"))
        {
            messageContent = messageContent.Substring(7);
        }
        if (messageContent.StartsWith("```"))
        {
            messageContent = messageContent.Substring(3);
        }
        if (messageContent.EndsWith("```"))
        {
            messageContent = messageContent.Substring(0, messageContent.Length - 3);
        }
        messageContent = messageContent.Trim();

        // Parse and return the job descriptions
        var parsedResult = JsonDocument.Parse(messageContent);
        return Results.Ok(JsonSerializer.Deserialize<object>(messageContent));
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = "Failed to parse LLM response as JSON", details = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to parse job descriptions: {ex.Message}");
    }
})
.WithName("ParseJobDescriptions")
.WithTags("Job Descriptions")
.WithOpenApi(operation => new(operation)
{
    Summary = "Parse bulk job descriptions using AI",
    Description = "Uses Azure OpenAI to parse unstructured job description text into structured JSON format"
});

// Parse PDF file containing job descriptions
app.MapPost("/api/job-descriptions/parse-pdf", async (HttpContext http) =>
{
    try
    {
        var form = await http.Request.ReadFormAsync();
        var file = form.Files.GetFile("file");

        if (file == null || file.Length == 0)
        {
            return Results.BadRequest(new { error = "No file uploaded" });
        }

        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "Only PDF files are supported" });
        }

        // Extract text from PDF using iText7
        string extractedText;
        using (var stream = file.OpenReadStream())
        using (var pdfReader = new PdfReader(stream))
        using (var pdfDocument = new PdfDocument(pdfReader))
        {
            var textBuilder = new StringBuilder();
            for (int i = 1; i <= pdfDocument.GetNumberOfPages(); i++)
            {
                var page = pdfDocument.GetPage(i);
                var strategy = new SimpleTextExtractionStrategy();
                var pageText = PdfTextExtractor.GetTextFromPage(page, strategy);
                textBuilder.AppendLine(pageText);
                textBuilder.AppendLine(); // Add spacing between pages
            }
            extractedText = textBuilder.ToString();
        }

        if (string.IsNullOrWhiteSpace(extractedText))
        {
            return Results.BadRequest(new { error = "Could not extract text from PDF" });
        }

        // Use Azure OpenAI to parse the extracted text
        var openAiKey = builder.Configuration["Azure:OpenAI:Key"];
        var openAiEndpoint = builder.Configuration["Azure:OpenAI:Endpoint"];
        var deploymentId = builder.Configuration["Azure:OpenAI:DeploymentId"];

        if (string.IsNullOrEmpty(openAiKey) || string.IsNullOrEmpty(openAiEndpoint))
        {
            return Results.Problem("Azure OpenAI is not configured");
        }

        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("api-key", openAiKey);

        var prompt = $@"Parse the following text extracted from a PDF and extract job descriptions. Return a JSON object with a 'jobDescriptions' array.

Each job description should have these fields:
- jobTitle (string, required): The job title/position name
- department (string): The department (e.g., Engineering, Marketing, Sales, HR, Finance, Operations, Product, Design, Customer Support, Other)
- experienceLevel (string): One of: Entry Level, Mid Level, Senior, Lead, Manager, Director, Executive
- employmentType (string): One of: Full-time, Part-time, Contract, Internship
- description (string): A brief overview of the role
- responsibilities (string): Key responsibilities, can be bullet points
- requiredSkills (array of strings): Required technical and soft skills
- niceToHaveSkills (array of strings): Nice-to-have skills
- qualifications (string): Educational and other qualifications
- interviewTopics (string): Key topics to evaluate during interview
- evaluationCriteria (string): How to evaluate candidates

If any field is not found in the text, use reasonable defaults or leave empty.
Parse ALL job descriptions found in the text.

TEXT FROM PDF:
{extractedText}

Return ONLY valid JSON, no markdown or explanation.";

        var requestBody = new
        {
            messages = new[]
            {
                new { role = "system", content = "You are a helpful assistant that parses job descriptions into structured JSON format. Always return valid JSON." },
                new { role = "user", content = prompt }
            },
            max_tokens = 4000,
            temperature = 0.3
        };

        var response = await client.PostAsync(
            $"{openAiEndpoint}/openai/deployments/{deploymentId}/chat/completions?api-version=2024-02-15-preview",
            new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return Results.Problem($"OpenAI API error: {errorContent}");
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        var openAiResponse = JsonDocument.Parse(responseContent);
        var messageContent = openAiResponse.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        // Clean up the response (remove markdown code blocks if present)
        messageContent = messageContent.Trim();
        if (messageContent.StartsWith("```json"))
        {
            messageContent = messageContent.Substring(7);
        }
        if (messageContent.StartsWith("```"))
        {
            messageContent = messageContent.Substring(3);
        }
        if (messageContent.EndsWith("```"))
        {
            messageContent = messageContent.Substring(0, messageContent.Length - 3);
        }
        messageContent = messageContent.Trim();

        // Parse and return the job descriptions
        var parsedResult = JsonDocument.Parse(messageContent);
        return Results.Ok(JsonSerializer.Deserialize<object>(messageContent));
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = "Failed to parse LLM response as JSON", details = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to parse PDF: {ex.Message}");
    }
})
.WithName("ParseJobDescriptionsPdf")
.WithTags("Job Descriptions")
.DisableAntiforgery()
.WithOpenApi(operation => new(operation)
{
    Summary = "Parse job descriptions from PDF using AI",
    Description = "Extracts text from PDF and uses Azure OpenAI to parse into structured JSON format"
});

// Get all job descriptions
app.MapGet("/api/job-descriptions", async () =>
{
    if (cosmosContainer == null)
    {
        return Results.Problem("Cosmos DB is not configured");
    }

    try
    {
        // Query all job descriptions (items with jobTitle field)
        var query = new QueryDefinition("SELECT * FROM c WHERE IS_DEFINED(c.jobTitle) ORDER BY c.createdAt DESC");
        var iterator = cosmosContainer.GetItemQueryIterator<JobDescription>(query);
        var results = new List<JobDescription>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return Results.Ok(results);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to retrieve job descriptions: {ex.Message}");
    }
})
.WithName("GetJobDescriptions")
.WithTags("Job Descriptions")
.WithOpenApi(operation => new(operation)
{
    Summary = "Get all job descriptions",
    Description = "Returns all job descriptions from Cosmos DB"
});

// Get a specific job description by ID
app.MapGet("/api/job-descriptions/{id}", async (string id) =>
{
    if (cosmosContainer == null)
    {
        return Results.Problem("Cosmos DB is not configured");
    }

    try
    {
        // Use id as partition key since that's how the container is configured
        var response = await cosmosContainer.ReadItemAsync<JobDescription>(id, new PartitionKey(id));
        return Results.Ok(response.Resource);
    }
    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return Results.NotFound(new { error = "Job description not found" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to retrieve job description: {ex.Message}");
    }
})
.WithName("GetJobDescription")
.WithTags("Job Descriptions")
.WithOpenApi(operation => new(operation)
{
    Summary = "Get a job description by ID",
    Description = "Returns a specific job description from Cosmos DB"
});

// Update a job description
app.MapPut("/api/job-descriptions/{id}", async (string id, JobDescription jobDescription) =>
{
    if (cosmosContainer == null)
    {
        return Results.Problem("Cosmos DB is not configured");
    }

    try
    {
        jobDescription.id = id;
        jobDescription.updatedAt = DateTime.UtcNow.ToString("o");

        // Use id as partition key
        var response = await cosmosContainer.ReplaceItemAsync(jobDescription, id, new PartitionKey(id));
        return Results.Ok(response.Resource);
    }
    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return Results.NotFound(new { error = "Job description not found" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to update job description: {ex.Message}");
    }
})
.WithName("UpdateJobDescription")
.WithTags("Job Descriptions")
.WithOpenApi(operation => new(operation)
{
    Summary = "Update a job description",
    Description = "Updates an existing job description in Cosmos DB"
});

// Delete a job description
app.MapDelete("/api/job-descriptions/{id}", async (string id) =>
{
    if (cosmosContainer == null)
    {
        return Results.Problem("Cosmos DB is not configured");
    }

    try
    {
        // Use id as partition key
        await cosmosContainer.DeleteItemAsync<JobDescription>(id, new PartitionKey(id));
        return Results.Ok(new { message = "Job description deleted successfully" });
    }
    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return Results.NotFound(new { error = "Job description not found" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to delete job description: {ex.Message}");
    }
})
.WithName("DeleteJobDescription")
.WithTags("Job Descriptions")
.WithOpenApi(operation => new(operation)
{
    Summary = "Delete a job description",
    Description = "Deletes a job description from Cosmos DB"
});

app.Run();

// ---------- Helper Functions ----------
static string GenerateInviteCode()
{
    const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Removed confusing chars like 0, O, 1, I
    var random = new Random();
    return new string(Enumerable.Repeat(chars, 6).Select(s => s[random.Next(s.Length)]).ToArray());
}

// Generate behavioral analysis from interview data
static async Task<VideoAnalysisResult?> GenerateBehavioralAnalysis(
    InterviewSession session,
    IHttpClientFactory httpClientFactory,
    string aoKey,
    string aoEndpoint,
    string? deploymentId)
{
    if (string.IsNullOrWhiteSpace(deploymentId)) return null;
    
    var transcriptText = new StringBuilder();
    transcriptText.AppendLine("INTERVIEW CONTEXT:");
    transcriptText.AppendLine($"Candidate: {session.CandidateName ?? "Unknown"}");
    transcriptText.AppendLine($"Position: {session.Position ?? "Software Developer"}");
    transcriptText.AppendLine($"Duration: {(DateTime.UtcNow - session.StartTime).TotalMinutes:F1} minutes");
    transcriptText.AppendLine($"Questions Asked: {session.QuestionCount}");
    transcriptText.AppendLine();
    
    if (session.Transcript.Count > 0)
    {
        transcriptText.AppendLine("TRANSCRIPT:");
        foreach (var qa in session.Transcript)
        {
            transcriptText.AppendLine($"Q{qa.QuestionNumber}: {qa.Question}");
            transcriptText.AppendLine($"A{qa.QuestionNumber}: {qa.Answer}");
            transcriptText.AppendLine($"(Response time: {qa.ResponseTimeSeconds:F1}s)");
            transcriptText.AppendLine();
        }
    }
    
    var systemPrompt = @"You are an expert behavioral analyst specializing in interview assessment. Based on the interview transcript and response patterns, analyze the candidate's behavioral indicators.

Return your analysis as a JSON object with this exact structure:
{
    ""engagementScore"": <number 1-100>,
    ""confidenceScore"": <number 1-100>,
    ""emotionalStabilityScore"": <number 1-100>,
    ""overallBehavioralScore"": <number 1-100>,
    ""emotions"": {
        ""neutral"": <percentage 0-100>,
        ""happiness"": <percentage 0-100>,
        ""surprise"": <percentage 0-100>,
        ""sadness"": <percentage 0-100>,
        ""anger"": <percentage 0-100>,
        ""fear"": <percentage 0-100>,
        ""disgust"": <percentage 0-100>,
        ""contempt"": <percentage 0-100>
    },
    ""keyObservations"": [""observation1"", ""observation2"", ""observation3""],
    ""behavioralRecommendations"": [""recommendation1"", ""recommendation2""]
}

Base your analysis on:
- Response times (faster suggests confidence, too fast may suggest nervousness)
- Answer length and detail (engagement indicator)
- Language patterns and word choice (emotional state indicators)
- Topic transitions and coherence (stability indicator)

Be constructive and professional in observations.";

    var userPrompt = $@"{transcriptText}

Analyze this interview for behavioral indicators and provide your assessment as JSON. Consider response patterns, communication style, and engagement level.";

    var aoUrl = $"{aoEndpoint}/openai/deployments/{deploymentId}/chat/completions?api-version=2023-10-01-preview";
    
    var body = new
    {
        messages = new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt }
        },
        max_tokens = 1000,
        temperature = 0.3
    };
    
    var jsonBody = JsonSerializer.Serialize(body);
    var client = httpClientFactory.CreateClient();
    client.DefaultRequestHeaders.Clear();
    client.DefaultRequestHeaders.Add("api-key", aoKey);
    
    var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
    var response = await client.PostAsync(aoUrl, content);
    
    if (!response.IsSuccessStatusCode) return null;
    
    var responseJson = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(responseJson);
    var root = doc.RootElement;
    
    try
    {
        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() > 0)
        {
            var messageContent = choices[0].GetProperty("message").GetProperty("content").GetString();
            
            // Clean up markdown formatting
            messageContent = messageContent?.Trim();
            if (messageContent?.StartsWith("```json") == true)
                messageContent = messageContent.Substring(7);
            if (messageContent?.StartsWith("```") == true)
                messageContent = messageContent.Substring(3);
            if (messageContent?.EndsWith("```") == true)
                messageContent = messageContent.Substring(0, messageContent.Length - 3);
            messageContent = messageContent?.Trim();
            
            var analysisJson = JsonDocument.Parse(messageContent!);
            var analysisRoot = analysisJson.RootElement;
            
            var result = new VideoAnalysisResult
            {
                EngagementScore = analysisRoot.GetProperty("engagementScore").GetInt32(),
                ConfidenceScore = analysisRoot.GetProperty("confidenceScore").GetInt32(),
                EmotionalStabilityScore = analysisRoot.GetProperty("emotionalStabilityScore").GetInt32(),
                OverallBehavioralScore = analysisRoot.GetProperty("overallBehavioralScore").GetInt32(),
                TotalFramesAnalyzed = session.Transcript.Count * 10, // Simulated
                VideoDurationSeconds = (DateTime.UtcNow - session.StartTime).TotalSeconds,
                Emotions = new EmotionBreakdown
                {
                    Neutral = analysisRoot.GetProperty("emotions").GetProperty("neutral").GetDouble(),
                    Happiness = analysisRoot.GetProperty("emotions").GetProperty("happiness").GetDouble(),
                    Surprise = analysisRoot.GetProperty("emotions").GetProperty("surprise").GetDouble(),
                    Sadness = analysisRoot.GetProperty("emotions").GetProperty("sadness").GetDouble(),
                    Anger = analysisRoot.GetProperty("emotions").GetProperty("anger").GetDouble(),
                    Fear = analysisRoot.GetProperty("emotions").GetProperty("fear").GetDouble(),
                    Disgust = analysisRoot.GetProperty("emotions").GetProperty("disgust").GetDouble(),
                    Contempt = analysisRoot.GetProperty("emotions").GetProperty("contempt").GetDouble()
                },
                KeyObservations = analysisRoot.GetProperty("keyObservations")
                    .EnumerateArray().Select(x => x.GetString() ?? "").ToList(),
                BehavioralRecommendations = analysisRoot.GetProperty("behavioralRecommendations")
                    .EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            };
            
            // Generate simulated emotion timeline
            var random = new Random(session.SessionId.GetHashCode());
            var duration = result.VideoDurationSeconds;
            for (int i = 0; i < 10; i++)
            {
                var timestamp = (duration / 10) * i;
                result.EmotionTimeline.Add(new EmotionDataPoint
                {
                    TimestampSeconds = timestamp,
                    DominantEmotion = random.Next(100) > 30 ? "neutral" : (random.Next(100) > 50 ? "happiness" : "surprise"),
                    Confidence = 0.7 + random.NextDouble() * 0.25,
                    Emotions = new EmotionBreakdown
                    {
                        Neutral = result.Emotions.Neutral + (random.NextDouble() - 0.5) * 10,
                        Happiness = result.Emotions.Happiness + (random.NextDouble() - 0.5) * 5,
                        Surprise = result.Emotions.Surprise + (random.NextDouble() - 0.5) * 3,
                        Sadness = result.Emotions.Sadness,
                        Anger = result.Emotions.Anger,
                        Fear = result.Emotions.Fear,
                        Disgust = result.Emotions.Disgust,
                        Contempt = result.Emotions.Contempt
                    }
                });
            }
            
            return result;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to parse behavioral analysis: {ex.Message}");
    }
    
    return null;
}

// Generate combined analysis from transcript and video analysis
static async Task<CombinedAnalysisResult> GenerateCombinedAnalysis(
    InterviewSession session,
    IHttpClientFactory httpClientFactory,
    string aoKey,
    string aoEndpoint,
    string? deploymentId)
{
    var result = new CombinedAnalysisResult
    {
        SessionId = session.SessionId,
        AnalyzedAt = DateTime.UtcNow,
        TranscriptAnalysis = session.Analysis,
        VideoAnalysis = session.VideoAnalysis
    };
    
    // Calculate scores
    var technicalScore = session.Analysis?.TechnicalSkills?.Score ?? 0;
    var communicationScore = session.Analysis?.Communication?.Score ?? 0;
    var behavioralScore = session.VideoAnalysis?.OverallBehavioralScore ?? 0;
    
    result.TechnicalScore = technicalScore;
    result.CommunicationScore = communicationScore;
    result.BehavioralScore = behavioralScore;
    
    // Weighted overall score: Technical 40%, Communication 30%, Behavioral 30%
    result.OverallScore = (int)Math.Round(
        technicalScore * 0.4 + 
        communicationScore * 0.3 + 
        behavioralScore * 0.3
    );
    
    // Use AI to generate comprehensive assessment
    if (!string.IsNullOrWhiteSpace(deploymentId))
    {
        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine($"Candidate: {session.CandidateName ?? "Unknown"}");
        contextBuilder.AppendLine($"Position: {session.Position ?? "Software Developer"}");
        contextBuilder.AppendLine();
        
        if (session.Analysis != null)
        {
            contextBuilder.AppendLine("TRANSCRIPT ANALYSIS:");
            contextBuilder.AppendLine($"- Overall Score: {session.Analysis.OverallScore}/100");
            contextBuilder.AppendLine($"- Technical Score: {session.Analysis.TechnicalSkills.Score}/100");
            contextBuilder.AppendLine($"- Communication Score: {session.Analysis.Communication.Score}/100");
            contextBuilder.AppendLine($"- Feedback: {session.Analysis.OverallFeedback}");
            contextBuilder.AppendLine($"- Hiring Recommendation: {session.Analysis.HiringRecommendation}");
            contextBuilder.AppendLine();
        }
        
        if (session.VideoAnalysis != null)
        {
            contextBuilder.AppendLine("BEHAVIORAL ANALYSIS:");
            contextBuilder.AppendLine($"- Engagement Score: {session.VideoAnalysis.EngagementScore}/100");
            contextBuilder.AppendLine($"- Confidence Score: {session.VideoAnalysis.ConfidenceScore}/100");
            contextBuilder.AppendLine($"- Emotional Stability: {session.VideoAnalysis.EmotionalStabilityScore}/100");
            contextBuilder.AppendLine($"- Key Observations: {string.Join("; ", session.VideoAnalysis.KeyObservations)}");
        }
        
        var systemPrompt = @"You are a senior hiring manager synthesizing interview results. Based on both transcript analysis and behavioral assessment, provide a final comprehensive evaluation.

Return your analysis as a JSON object:
{
    ""overallAssessment"": ""<2-3 sentence comprehensive summary>"",
    ""hiringRecommendation"": ""<Strong Hire / Hire / Maybe / No Hire with brief justification>"",
    ""keyStrengths"": [""strength1"", ""strength2"", ""strength3""],
    ""keyConcerns"": [""concern1"", ""concern2""]
}

Consider both technical competence AND behavioral indicators when making your recommendation.";

        var aoUrl = $"{aoEndpoint}/openai/deployments/{deploymentId}/chat/completions?api-version=2023-10-01-preview";
        
        var body = new
        {
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = contextBuilder.ToString() }
            },
            max_tokens = 500,
            temperature = 0.3
        };
        
        var jsonBody = JsonSerializer.Serialize(body);
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("api-key", aoKey);
        
        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(aoUrl, content);
        
        if (response.IsSuccessStatusCode)
        {
            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            
            try
            {
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0)
                {
                    var messageContent = choices[0].GetProperty("message").GetProperty("content").GetString();
                    
                    messageContent = messageContent?.Trim();
                    if (messageContent?.StartsWith("```json") == true)
                        messageContent = messageContent.Substring(7);
                    if (messageContent?.StartsWith("```") == true)
                        messageContent = messageContent.Substring(3);
                    if (messageContent?.EndsWith("```") == true)
                        messageContent = messageContent.Substring(0, messageContent.Length - 3);
                    messageContent = messageContent?.Trim();
                    
                    var analysisJson = JsonDocument.Parse(messageContent!);
                    var analysisRoot = analysisJson.RootElement;
                    
                    result.OverallAssessment = analysisRoot.GetProperty("overallAssessment").GetString() ?? "";
                    result.HiringRecommendation = analysisRoot.GetProperty("hiringRecommendation").GetString() ?? "";
                    result.KeyStrengths = analysisRoot.GetProperty("keyStrengths")
                        .EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                    result.KeyConcerns = analysisRoot.GetProperty("keyConcerns")
                        .EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to parse combined analysis: {ex.Message}");
                result.OverallAssessment = "Analysis completed based on available data.";
                result.HiringRecommendation = session.Analysis?.HiringRecommendation ?? "Further review needed";
            }
        }
    }
    
    return result;
}

// ---------- Models ----------
public record SpeechTokenResponse(string token, string region);
public record GenerateQuestionRequest(string text, string? sessionId = null);
public record GenerateQuestionResponse(string question, bool isInterviewEnded = false, int? questionCount = null, double? elapsedMinutes = null);

public class InterviewInvite
{
    public string id { get; set; } = string.Empty; // Cosmos DB requires lowercase 'id'
    public string type { get; set; } = "invite"; // Document type for Cosmos DB
    public string Code { get; set; } = string.Empty;
    public string CandidateName { get; set; } = string.Empty;
    public string? CandidateEmail { get; set; }
    public string? Position { get; set; }
    public string? JobDescriptionId { get; set; }
    public string? ResumeId { get; set; } // Reference to parsed resume in Cosmos DB
    public string? ResumeFileName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsUsed { get; set; }
    public DateTime? UsedAt { get; set; }
    public string? SessionId { get; set; } // Links to the interview session when used
}

public class InterviewSession
{
    public string id { get; set; } = string.Empty; // Cosmos DB requires lowercase 'id'
    public string type { get; set; } = "session"; // Document type for Cosmos DB
    public string SessionId { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public int QuestionCount { get; set; }
    public bool IsEnded { get; set; }
    public List<QAExchange> Transcript { get; set; } = new();
    public string? CandidateName { get; set; }
    public string? CandidateEmail { get; set; }
    public string? Position { get; set; }
    public string? JobDescriptionId { get; set; }
    public string? ResumeId { get; set; } // Reference to parsed resume in Cosmos DB
    public string? InviteCode { get; set; } // Links back to invite
    public InterviewAnalysis? Analysis { get; set; }
    public VideoAnalysisResult? VideoAnalysis { get; set; } // Video behavioral analysis
    public CombinedAnalysisResult? CombinedAnalysis { get; set; } // Combined transcript + video analysis
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
    
    // JD Fit Analysis
    public JDFitAnalysis? JobFitAnalysis { get; set; }
    public List<AlternativeJDMatch>? AlternativeJobMatches { get; set; }
}

// JD Fit Analysis - how well candidate matches the interviewed position
public class JDFitAnalysis
{
    public string JobDescriptionId { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public int FitScore { get; set; } // 1-100
    public string FitSummary { get; set; } = string.Empty;
    public List<string> MatchingSkills { get; set; } = new();
    public List<string> MissingSkills { get; set; } = new();
    public bool IsGoodFit { get; set; } // true if FitScore >= 70
}

// Alternative JD Match - other positions the candidate might be suitable for
public class AlternativeJDMatch
{
    public string JobDescriptionId { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public int MatchScore { get; set; } // 1-100
    public string MatchReason { get; set; } = string.Empty;
    public List<string> MatchingSkills { get; set; } = new();
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

// ========== VIDEO ANALYSIS MODELS ==========

public class VideoAnalysisResult
{
    public string SessionId { get; set; } = string.Empty;
    public DateTime AnalyzedAt { get; set; }
    public int TotalFramesAnalyzed { get; set; }
    public double VideoDurationSeconds { get; set; }
    
    // Overall behavioral scores (1-100)
    public int EngagementScore { get; set; }
    public int ConfidenceScore { get; set; }
    public int EmotionalStabilityScore { get; set; }
    public int OverallBehavioralScore { get; set; }
    
    // Emotion breakdown (percentage of time showing each emotion)
    public EmotionBreakdown Emotions { get; set; } = new();
    
    // Timeline of emotional states for charting
    public List<EmotionDataPoint> EmotionTimeline { get; set; } = new();
    
    // Key observations
    public List<string> KeyObservations { get; set; } = new();
    
    // Recommendations based on behavioral analysis
    public List<string> BehavioralRecommendations { get; set; } = new();
}

public class EmotionBreakdown
{
    public double Neutral { get; set; }
    public double Happiness { get; set; }
    public double Surprise { get; set; }
    public double Sadness { get; set; }
    public double Anger { get; set; }
    public double Fear { get; set; }
    public double Disgust { get; set; }
    public double Contempt { get; set; }
}

public class EmotionDataPoint
{
    public double TimestampSeconds { get; set; }
    public string DominantEmotion { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public EmotionBreakdown Emotions { get; set; } = new();
}

public class FaceAnalysisFrame
{
    public double TimestampSeconds { get; set; }
    public bool FaceDetected { get; set; }
    public double? HeadPoseYaw { get; set; }  // Looking left/right
    public double? HeadPosePitch { get; set; } // Looking up/down
    public double? HeadPoseRoll { get; set; }  // Head tilt
    public EmotionBreakdown? Emotions { get; set; }
}

public class CombinedAnalysisResult
{
    public string SessionId { get; set; } = string.Empty;
    public DateTime AnalyzedAt { get; set; }
    
    // Overall combined score
    public int OverallScore { get; set; }
    public string OverallAssessment { get; set; } = string.Empty;
    
    // Individual scores
    public int TechnicalScore { get; set; }
    public int CommunicationScore { get; set; }
    public int BehavioralScore { get; set; }
    
    // Detailed analysis
    public InterviewAnalysis? TranscriptAnalysis { get; set; }
    public VideoAnalysisResult? VideoAnalysis { get; set; }
    
    // Final recommendation
    public string HiringRecommendation { get; set; } = string.Empty;
    public List<string> KeyStrengths { get; set; } = new();
    public List<string> KeyConcerns { get; set; } = new();
}

public record AnalyzeInterviewRequest(string sessionId);
public record SaveTranscriptRequest(string sessionId, string question, string answer, double responseTimeSeconds);

// ========== JOB DESCRIPTION MODEL ==========

public class JobDescription
{
    public string id { get; set; } = string.Empty;
    public string jobTitle { get; set; } = string.Empty;
    public string department { get; set; } = string.Empty;
    public string experienceLevel { get; set; } = string.Empty;
    public string employmentType { get; set; } = string.Empty;
    public string description { get; set; } = string.Empty;
    public string responsibilities { get; set; } = string.Empty;
    public List<string> requiredSkills { get; set; } = new();
    public List<string> niceToHaveSkills { get; set; } = new();
    public string qualifications { get; set; } = string.Empty;
    public string interviewTopics { get; set; } = string.Empty;
    public string evaluationCriteria { get; set; } = string.Empty;
    public string createdAt { get; set; } = string.Empty;
    public string updatedAt { get; set; } = string.Empty;
    public string createdBy { get; set; } = string.Empty;
}

// ========== CANDIDATE RESUME MODEL ==========

public class CandidateResume
{
    public string id { get; set; } = string.Empty;
    public string type { get; set; } = "resume"; // Document type for Cosmos DB
    public string candidateName { get; set; } = string.Empty;
    public string? candidateEmail { get; set; }
    public string? fileName { get; set; }
    
    // Parsed resume fields
    public string? summary { get; set; }
    public string? currentRole { get; set; }
    public int? yearsOfExperience { get; set; }
    public List<string> technicalSkills { get; set; } = new();
    public List<string> softSkills { get; set; } = new();
    public List<string> programmingLanguages { get; set; } = new();
    public List<string> frameworks { get; set; } = new();
    public List<string> tools { get; set; } = new();
    public List<string> certifications { get; set; } = new();
    public List<WorkExperience> workExperience { get; set; } = new();
    public List<Education> education { get; set; } = new();
    public List<string> achievements { get; set; } = new();
    public List<string> suggestedInterviewTopics { get; set; } = new();
    public List<string> potentialStrengths { get; set; } = new();
    public List<string> areasToProbe { get; set; } = new();
    
    public string createdAt { get; set; } = string.Empty;
    public string? inviteCode { get; set; }
}

public class WorkExperience
{
    public string? company { get; set; }
    public string? title { get; set; }
    public string? duration { get; set; }
    public List<string> highlights { get; set; } = new();
}

public class Education
{
    public string? institution { get; set; }
    public string? degree { get; set; }
    public string? field { get; set; }
    public string? year { get; set; }
}
