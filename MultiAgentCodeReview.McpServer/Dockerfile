# ============================================================
# Stage 1 — Build
# Build context must be the REPO ROOT:
#   docker build -f MultiAgentCodeReview.McpServer/Dockerfile -t multiagent-code-review:local .
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution + project files first so `dotnet restore` is layer-cached.
# Code changes then do not invalidate the package-download layer.
COPY MultiAgentCodeReview.slnx ./
COPY MultiAgentCodeReview.Core/MultiAgentCodeReview.Core.csproj MultiAgentCodeReview.Core/
COPY MultiAgentCodeReview.Agents/MultiAgentCodeReview.Agents.csproj MultiAgentCodeReview.Agents/
COPY MultiAgentCodeReview.Orchestration/MultiAgentCodeReview.Orchestration.csproj MultiAgentCodeReview.Orchestration/
COPY MultiAgentCodeReview.Host/MultiAgentCodeReview.Host.csproj MultiAgentCodeReview.Host/
COPY MultiAgentCodeReview.McpServer/MultiAgentCodeReview.McpServer.csproj MultiAgentCodeReview.McpServer/
COPY MultiAgentCodeReview.Api/MultiAgentCodeReview.Api.csproj MultiAgentCodeReview.Api/

RUN dotnet restore MultiAgentCodeReview.slnx

# Now copy the full source and publish the MCP server
COPY . .

RUN dotnet publish MultiAgentCodeReview.McpServer/MultiAgentCodeReview.McpServer.csproj \
        -c Release -o /app/publish --no-restore

# ============================================================
# Stage 2 — Runtime
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# git is required at REQUEST TIME — RepositorySourceResolver runs `git clone` inside
# the running container when a caller passes a repo URL instead of a local path
# (remote/HTTP callers have no shared filesystem with the server). apt-get needs
# root, so this runs BEFORE switching to the non-root user.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# The official .NET 8+ runtime images ship with a built-in non-root
# user named "app" (UID 1654). Use it — never run as root.
USER app

COPY --from=build /app/publish ./

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV MCP_TRANSPORT=http

ENTRYPOINT ["dotnet", "MultiAgentCodeReview.McpServer.dll"]
