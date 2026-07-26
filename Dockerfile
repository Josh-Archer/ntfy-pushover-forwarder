FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/NtfyPushoverForwarder.csproj .
RUN dotnet restore
COPY src/ .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

# Run as non-root user for security best practices
USER $APP_UID

ENTRYPOINT ["dotnet", "NtfyPushoverForwarder.dll"]
