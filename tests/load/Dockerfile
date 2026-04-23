FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution file and all project files in the Fleans.Api dependency closure.
# Order: solution first, then all .csproj files, so dotnet restore is fully layer-cached.
COPY Fleans.sln global.json ./
COPY Fleans.Domain/Fleans.Domain.csproj                                   Fleans.Domain/
COPY Fleans.Application/Fleans.Application.csproj                         Fleans.Application/
COPY Fleans.Infrastructure/Fleans.Infrastructure.csproj                   Fleans.Infrastructure/
COPY Fleans.Persistence/Fleans.Persistence.csproj                         Fleans.Persistence/
COPY Fleans.Persistence.Sqlite/Fleans.Persistence.Sqlite.csproj           Fleans.Persistence.Sqlite/
COPY Fleans.Persistence.PostgreSql/Fleans.Persistence.PostgreSql.csproj   Fleans.Persistence.PostgreSql/
COPY Fleans.ServiceDefaults/Fleans.ServiceDefaults.csproj                 Fleans.ServiceDefaults/
COPY Fleans.Api/Fleans.Api.csproj                                         Fleans.Api/
RUN dotnet restore Fleans.Api/Fleans.Api.csproj

# Copy source for all projects in the closure and publish.
COPY Fleans.Domain/             Fleans.Domain/
COPY Fleans.Application/        Fleans.Application/
COPY Fleans.Infrastructure/     Fleans.Infrastructure/
COPY Fleans.Persistence/        Fleans.Persistence/
COPY Fleans.Persistence.Sqlite/ Fleans.Persistence.Sqlite/
COPY Fleans.Persistence.PostgreSql/ Fleans.Persistence.PostgreSql/
COPY Fleans.ServiceDefaults/    Fleans.ServiceDefaults/
COPY Fleans.Api/                Fleans.Api/
RUN dotnet publish Fleans.Api/Fleans.Api.csproj -c Release -o /app/publish --no-restore

# aspnet:10.0 includes curl, used by the Docker Compose health check.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Fleans.Api.dll"]
