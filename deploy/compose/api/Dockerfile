FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props BannedSymbols.txt OpenPayments.slnx ./
COPY src/OpenPayments.Domain/OpenPayments.Domain.csproj src/OpenPayments.Domain/
COPY src/OpenPayments.Transitions/OpenPayments.Transitions.csproj src/OpenPayments.Transitions/
COPY src/OpenPayments.Psp/OpenPayments.Psp.csproj src/OpenPayments.Psp/
COPY src/OpenPayments.Api/OpenPayments.Api.csproj src/OpenPayments.Api/
RUN dotnet restore src/OpenPayments.Api/OpenPayments.Api.csproj
COPY src ./src
RUN dotnet publish src/OpenPayments.Api/OpenPayments.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "OpenPayments.Api.dll"]
