FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Axon.Core/ Axon.Core/
COPY Axon.Client/ Axon.Client/
COPY examples/Axon.Example.Client/ examples/Axon.Example.Client/
RUN dotnet publish examples/Axon.Example.Client/Axon.Example.Client.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Axon.Example.Client.dll"]
