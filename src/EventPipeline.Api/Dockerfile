FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Un único COPY del árbol de fuentes en /src/src (copiar a ./ colocaría los
# proyectos directamente en el WORKDIR y las rutas del publish no cuadrarían).
COPY src/ ./src/
RUN dotnet publish src/EventPipeline.Api/EventPipeline.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "EventPipeline.Api.dll"]
