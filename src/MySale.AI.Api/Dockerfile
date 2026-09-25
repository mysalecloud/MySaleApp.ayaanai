# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["nuget.config", "."]
COPY ["src/MySale.AI.Api/MySale.AI.Api.csproj", "src/MySale.AI.Api/"]
COPY ["src/MySale.AI.Infrastructure/MySale.AI.Infrastructure.csproj", "src/MySale.AI.Infrastructure/"]
COPY ["src/MySale.AI.Application/MySale.AI.Application.csproj", "src/MySale.AI.Application/"]
COPY ["src/MySale.AI.Domain/MySale.AI.Domain.csproj", "src/MySale.AI.Domain/"]
RUN dotnet restore "./src/MySale.AI.Api/MySale.AI.Api.csproj"
COPY . .
WORKDIR "/src/src/MySale.AI.Api"
RUN dotnet build "./MySale.AI.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./MySale.AI.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MySale.AI.Api.dll"]