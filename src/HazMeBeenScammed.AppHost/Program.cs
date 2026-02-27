var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.HazMeBeenScammed_Api>("api");

builder.AddProject<Projects.HazMeBeenScammed_Web>("web")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
