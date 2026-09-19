FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Axon.Core/ Axon.Core/
COPY Axon.Client/ Axon.Client/
COPY Axon.Server/ Axon.Server/
COPY Axon.Server.Redis/ Axon.Server.Redis/
COPY Axon.Server.OpenTelemetry/ Axon.Server.OpenTelemetry/
COPY Axon.Store.SqlServer/ Axon.Store.SqlServer/
COPY examples/Axon.Example.Server/ examples/Axon.Example.Server/
RUN dotnet publish examples/Axon.Example.Server/Axon.Example.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Axon.Example.Server.dll"]
