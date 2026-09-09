# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the entire project to utilize local NuGet cache
COPY . .

# Restore packages (will use local cache if available)
# If network is unavailable, it will fall back to cached packages
RUN dotnet restore --disable-parallel

# Build and publish
RUN dotnet publish -c release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Expose port
ENV ASPNETCORE_URLS=http://+:8000
EXPOSE 8000

# Run the application
ENTRYPOINT ["dotnet", "ChatBot.Api.dll"]
