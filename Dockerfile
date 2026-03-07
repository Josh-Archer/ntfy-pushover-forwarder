FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/NtfyPushoverForwarder.csproj .
RUN dotnet restore
COPY src/ .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Run as non-root user for security best practices
USER $APP_UID

ENTRYPOINT ["dotnet", "NtfyPushoverForwarder.dll"]
