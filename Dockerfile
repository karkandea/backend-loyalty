FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY BackendLoyalty.sln ./
COPY src/BackendLoyalty.Api/BackendLoyalty.Api.csproj src/BackendLoyalty.Api/
COPY src/BackendLoyalty.Application/BackendLoyalty.Application.csproj src/BackendLoyalty.Application/
COPY src/BackendLoyalty.Domain/BackendLoyalty.Domain.csproj src/BackendLoyalty.Domain/
COPY src/BackendLoyalty.Infrastructure/BackendLoyalty.Infrastructure.csproj src/BackendLoyalty.Infrastructure/
RUN dotnet restore BackendLoyalty.sln

COPY src ./src
RUN dotnet publish src/BackendLoyalty.Api/BackendLoyalty.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "BackendLoyalty.Api.dll"]
