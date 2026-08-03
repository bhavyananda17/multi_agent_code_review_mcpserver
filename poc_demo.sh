#!/bin/bash
# POC Demo — runs MCP server standalone to prove reviews happen in-process

REPO_PATH="${1:-/Users/bhavyananda17/Documents/coding/system-information-viewer}"
echo "=== POC Demo: Multi-Agent Code Review via MCP Server ==="
echo "OpenCode is NOT running — only 'dotnet' (McpServer) does the work"
echo "Repo: $REPO_PATH"
echo ""

printf '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"poc-demo","version":"1.0"}}}\n{"jsonrpc":"2.0","method":"notifications/initialized"}\n{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"review_repo","arguments":{"repo_path":"'"$REPO_PATH"'","commit_hash":"HEAD"}}}\n' | dotnet run --project MultiAgentCodeReview.McpServer 2>&1
