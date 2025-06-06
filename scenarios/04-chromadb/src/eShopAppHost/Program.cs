using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var sqldb = builder.AddSqlServer("sql")
    .WithDataVolume()
    .AddDatabase("sqldb");

var chromaDB = builder.AddContainer("chroma", "chromadb/chroma")
    .WithHttpEndpoint(port: 8000, targetPort: 8000, name: "chromaendpoint")
    .WithLifetime(ContainerLifetime.Persistent)
    .PublishAsContainer();

var endpoint = chromaDB.GetEndpoint("chromaendpoint");

var products = builder.AddProject<Projects.Products>("products")
    .WithReference(endpoint)
    .WithReference(sqldb)
    .WaitFor(sqldb); 

var store = builder.AddProject<Projects.Store>("store")
    .WithReference(products)
    .WaitFor(products)
    .WithExternalHttpEndpoints();

if (builder.ExecutionContext.IsPublishMode)
{
    // production code uses Azure services, so we need to add them here
    var appInsights = builder.AddAzureApplicationInsights("appInsights");
    var chatDeploymentName = "gpt-41-mini";
    var embeddingsDeploymentName = "text-embedding-ada-002";
    var aoai = builder.AddAzureOpenAI("openai");
    var gpt41mini = aoai.AddDeployment(name: chatDeploymentName,
            modelName: "gpt-4.1-mini",
            modelVersion: "2025-04-14");
    gpt41mini.Resource.SkuCapacity = 10;
    gpt41mini.Resource.SkuName = "GlobalStandard";

    var embeddingsDeployment = aoai.AddDeployment(name: embeddingsDeploymentName,
        modelName: "text-embedding-ada-002",
        modelVersion: "2");


    products.WithReference(appInsights)
        .WithReference(aoai)
        .WithEnvironment("AI_ChatDeploymentName", chatDeploymentName)
        .WithEnvironment("AI_embeddingsDeploymentName", embeddingsDeploymentName);

    store.WithReference(appInsights)
        .WithExternalHttpEndpoints();
}

builder.Build().Run();
