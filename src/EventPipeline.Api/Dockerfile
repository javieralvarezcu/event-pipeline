FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/EventPipeline.Api/EventPipeline.Api.csproj src/EventPipeline.Api/
COPY src/EventPipeline.Core/EventPipeline.Core.csproj src/EventPipeline.Core/
RUN dotnet restore src/EventPipeline.Api/EventPipeline.Api.csproj
COPY src/ ./
RUN dotnet publish src/EventPipeline.Api/EventPipeline.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "EventPipeline.Api.dll"]
