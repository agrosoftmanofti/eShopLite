using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.KernelMemory;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using Products.Endpoints;
using Products.Memory;
using Products.Models;
using System.Web;

// Constants for clarity
const string secretSectionNameOpenAI = "openai";
const string secretSectionNameDeepSeekR1 = "deepseekr1";

const string deploymentNameAzureOpenAIChat = "gpt-41-mini";
const string deploymentNameAzureOpenAIEmbeddings = "text-embedding-ada-002";
const string deploymentNameDeepSeekR1 = "DeepSeek-R1";

const string chatClientNameOpenAI = "chatClientOpenAI";
const string chatClientNameDeepSeekR1 = "chatClientDeepSeekR1";

var builder = WebApplication.CreateBuilder(args);

// Disable Globalization Invariant Mode
Environment.SetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "false");

// Add default services
builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

// Add DbContext service
builder.AddSqlServerDbContext<Context>("sqldb");

// add OpenAI services
builder.AddAzureOpenAIClient(secretSectionNameOpenAI);

// register Embeddings client for OpenAI
builder.Services.AddSingleton<EmbeddingClient>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    logger.LogInformation($"Embedding client configuration, modelId: {deploymentNameAzureOpenAIEmbeddings}");
    try
    {
        var client = sp.GetRequiredService<OpenAIClient>();
        return client.GetEmbeddingClient(deploymentNameAzureOpenAIEmbeddings);
    }
    catch (Exception exc)
    {
        logger.LogError(exc, "Error creating embeddings client");
        throw;
    }
});

// Register ChatClient for OpenAI
builder.Services.AddActivatedKeyedSingleton<ChatClient>(chatClientNameOpenAI, (sp, _) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    logger.LogInformation($"Chat client configuration, modelId: {deploymentNameAzureOpenAIChat}");
    try
    {
        var client = sp.GetRequiredService<OpenAIClient>();
        return client.GetChatClient(deploymentNameAzureOpenAIChat);
    }
    catch (Exception exc)
    {
        logger.LogError(exc, "Error creating chat client");
        throw;
    }
});

// Register ChatClient for DeepSeekR1
builder.Services.AddActivatedKeyedSingleton<ChatClient>(chatClientNameDeepSeekR1, (sp, _) =>
{
    var logger = sp.GetService<ILogger<Program>>()!;
    logger.LogInformation($"Register ChatClient for DeepSeekR1");
    logger.LogInformation($"Chat client configuration, modelId: {deploymentNameDeepSeekR1}");
    try
    {
        var (endpoint, apiKey) = GetEndpointAndKey(builder, secretSectionNameDeepSeekR1, logger);

        // log the values of the endpoint and apiKey, but do not log the apiKey itself
        logger.LogInformation($"Endpoint: {endpoint}");
        logger.LogInformation($"ApiKey IsNullOrEmpty: {string.IsNullOrEmpty(apiKey)}");

        if (string.IsNullOrEmpty(apiKey))
        {
            // no apikey, use default azure credential  
            var endpointModel = new Uri(endpoint);
            logger.LogInformation($"No ApiKey, use default azure credentials.");
            logger.LogInformation($"Creating DeepSeekR1 chat client with modelId: [{deploymentNameDeepSeekR1}] / endpoint: [{endpoint}]");

            var credential = new DefaultAzureCredential();
            var client = new AzureOpenAIClient(
                endpoint: endpointModel,
                credential: credential);
            return client.GetChatClient(deploymentNameDeepSeekR1);
        }
        else
        {
            // using ApiKey
            logger.LogInformation($"ApiKey Found, use ApiKey credentials.");
            logger.LogInformation($"Creating DeepSeekR1 chat client with modelId: [{deploymentNameDeepSeekR1}] / endpoint: [{endpoint}]");
            var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
            var client = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), options);
            return client.GetChatClient(deploymentNameDeepSeekR1);
        }
    }
    catch (Exception exc)
    {
        logger.LogError(exc, "Error creating DeepSeekR1 chat client");
        return null!;
    }
});

// Register a singleton for Kernel Memory with the AzureOpenAI configuration
builder.Services.AddSingleton<IKernelMemory>(sp =>
{
    var logger = sp.GetService<ILogger<Program>>()!;
    logger.LogInformation($"Creating kernel memory: {deploymentNameAzureOpenAIChat}");
    try
    {
        // Configure Text Generation
        var configText = GetAzureOpenAIConfig(builder, secretSectionNameOpenAI, logger);
        configText.Deployment = deploymentNameAzureOpenAIChat;
        configText.APIType = AzureOpenAIConfig.APITypes.TextCompletion;

        // Configure Embedding Generation
        var configEmbeddings = GetAzureOpenAIConfig(builder, secretSectionNameOpenAI, logger);
        configEmbeddings.Deployment = deploymentNameAzureOpenAIEmbeddings;
        configEmbeddings.APIType = AzureOpenAIConfig.APITypes.EmbeddingGeneration;

        // Build kernel memory
        return new KernelMemoryBuilder()
            .WithAzureOpenAITextGeneration(configText)
            .WithAzureOpenAITextEmbeddingGeneration(configEmbeddings)
            .Build();
    }
    catch (Exception exc)
    {
        logger.LogError(exc, "Error creating kernel memory");
        return null!;
    }
});

builder.Services.AddSingleton<IConfiguration>(_ => builder.Configuration);

// Register MemoryContext
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetService<ILogger<Program>>();
    logger.LogInformation("Creating memory context");
    return new MemoryContext(
        logger,
        sp.GetKeyedService<ChatClient>(chatClientNameOpenAI),
        sp.GetKeyedService<ChatClient>(chatClientNameDeepSeekR1),
        sp.GetRequiredService<EmbeddingClient>(),
        sp.GetRequiredService<IKernelMemory>());
});

var app = builder.Build();

// Initialize default endpoints
app.MapDefaultEndpoints();

// Configure request pipeline
app.UseHttpsRedirection();
app.MapProductEndpoints();
app.UseStaticFiles();

// Log Azure OpenAI and DeepSeek resources
app.Logger.LogInformation($"Azure OpenAI resources -> OpenAI secret section name: {secretSectionNameOpenAI}");
AppContext.SetSwitch("OpenAI.Experimental.EnableOpenTelemetry", true);
app.Logger.LogInformation($"Azure DeepSeek-R1 resources -> DeepSeek-R1 secret section name: {secretSectionNameDeepSeekR1}");

// Manage database
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<Context>();
    try
    {
        app.Logger.LogInformation("Ensure database created");
        context.Database.EnsureCreated();
    }
    catch (Exception exc)
    {
        app.Logger.LogError(exc, "Error creating database");
    }
    DbInitializer.Initialize(context);

    app.Logger.LogInformation("Start fill products in vector db");
    var memoryContext = app.Services.GetRequiredService<MemoryContext>();
    await memoryContext.InitMemoryContextAsync(context);
    app.Logger.LogInformation("Done fill products in vector db");
}

app.Run();

static AzureOpenAIConfig GetAzureOpenAIConfig(WebApplicationBuilder builder, string name, ILogger<Program> logger)
{
    var (endpoint, apiKey) = GetEndpointAndKey(builder, name, logger);
    return string.IsNullOrEmpty(apiKey)
        ? new AzureOpenAIConfig
        {
            Endpoint = endpoint,
            Auth = AzureOpenAIConfig.AuthTypes.AzureIdentity,
        }
        : new AzureOpenAIConfig
        {
            APIKey = apiKey,
            Endpoint = endpoint,
            Auth = AzureOpenAIConfig.AuthTypes.APIKey
        };
}

static (string endpoint, string apiKey) GetEndpointAndKey(WebApplicationBuilder builder, string name, ILogger<Program> logger)
{
    try
    {
        logger.LogInformation($"Get endpoint and key for [{name}]");
        var connectionString = builder.Configuration.GetConnectionString(name);
        var parameters = HttpUtility.ParseQueryString(connectionString.Replace(";", "&"));

        // Ensure non-null values for endpoint, key can be null
        var endpoint = parameters["Endpoint"] ?? throw new InvalidOperationException($"Endpoint is missing for [{name}]");
        var apiKey = parameters["Key"] ?? string.Empty;

        return (endpoint, apiKey);
    }
    catch (Exception exc)
    {
        logger.LogError(exc, $"Error retrieving endpoint and key for [{name}]");
        throw;
    }
}
