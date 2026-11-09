FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props BannedSymbols.txt OpenPayments.slnx ./
COPY src/OpenPayments.Console/OpenPayments.Console.csproj src/OpenPayments.Console/
RUN dotnet restore src/OpenPayments.Console/OpenPayments.Console.csproj
COPY src/OpenPayments.Console ./src/OpenPayments.Console
RUN dotnet publish src/OpenPayments.Console/OpenPayments.Console.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8090
ENTRYPOINT ["dotnet", "OpenPayments.Console.dll"]
