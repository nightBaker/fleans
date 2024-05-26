var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Fleans_Api>("api");

builder.Build().Run();
