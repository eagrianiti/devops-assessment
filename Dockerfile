# --- Stage 1: build ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only the csproj first to leverage Docker layer caching for restore
COPY src/SampleApp/SampleApp.csproj ./SampleApp/
RUN dotnet restore ./SampleApp/SampleApp.csproj

# Copy the rest of the source and publish
COPY src/SampleApp/. ./SampleApp/
WORKDIR /src/SampleApp
RUN dotnet publish SampleApp.csproj -c Release -o /app/publish --no-restore

# --- Stage 2: runtime (aspnet runtime only, no SDK -> smaller, safer image) ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Run as non-root for security
RUN adduser --disabled-password --gecos "" appuser
USER appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["dotnet", "SampleApp.dll"]
