FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/TNovCloud.Shared/TNovCloud.Shared.csproj src/TNovCloud.Shared/
COPY src/TNovCloud.Api/TNovCloud.Api.csproj src/TNovCloud.Api/
RUN dotnet restore src/TNovCloud.Api/TNovCloud.Api.csproj
COPY src/TNovCloud.Shared/ src/TNovCloud.Shared/
COPY src/TNovCloud.Api/ src/TNovCloud.Api/
RUN dotnet publish src/TNovCloud.Api/TNovCloud.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TNovCloud.Api.dll"]
