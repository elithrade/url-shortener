FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Shortener/Shortener.csproj src/Shortener/
RUN dotnet restore src/Shortener/Shortener.csproj
COPY src/Shortener/ src/Shortener/
RUN dotnet publish src/Shortener/Shortener.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Shortener.dll"]
