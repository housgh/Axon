FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Axon.Core/ Axon.Core/
COPY Axon.Client/ Axon.Client/
COPY Axon.Server/ Axon.Server/
COPY Axon.Store.SqlServer/ Axon.Store.SqlServer/
COPY examples/Axon.Example.Monolith/ examples/Axon.Example.Monolith/
RUN dotnet publish examples/Axon.Example.Monolith/Axon.Example.Monolith.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Axon.Example.Monolith.dll"]
