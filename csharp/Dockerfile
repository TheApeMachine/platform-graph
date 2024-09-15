# Use an official .NET runtime as a parent image
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

# Set the working directory inside the container
WORKDIR /analyzer

# Copy the current directory contents into the container at /app
COPY CSharpAnalyzer/ ./

RUN dotnet add package Neo4j.Driver \
    && dotnet add package Microsoft.CodeAnalysis.CSharp.Workspaces \
    && dotnet add package Microsoft.CodeAnalysis.CSharp \
    && dotnet add package Microsoft.Build.Locator \
    && dotnet add package Microsoft.Build \
    && dotnet add package Microsoft.Build.Framework \
    && dotnet add package Microsoft.Build.Tasks.Core \
    && dotnet add package Microsoft.CodeAnalysis.Workspaces.MSBuild \
    && dotnet restore \
    && dotnet build --configuration Release

# Run the application
CMD ["dotnet", "run", "--project", "CSharpAnalyzer.csproj"]