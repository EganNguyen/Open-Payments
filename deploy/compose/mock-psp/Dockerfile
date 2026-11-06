FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props BannedSymbols.txt OpenPayments.slnx ./
COPY tools/OpenPayments.MockPsp/OpenPayments.MockPsp.csproj tools/OpenPayments.MockPsp/
RUN dotnet restore tools/OpenPayments.MockPsp/OpenPayments.MockPsp.csproj
COPY tools ./tools
RUN dotnet publish tools/OpenPayments.MockPsp/OpenPayments.MockPsp.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "OpenPayments.MockPsp.dll"]
