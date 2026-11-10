FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props BannedSymbols.txt OpenPayments.slnx ./
COPY src/OpenPayments.Domain/OpenPayments.Domain.csproj src/OpenPayments.Domain/
COPY src/OpenPayments.Transitions/OpenPayments.Transitions.csproj src/OpenPayments.Transitions/
COPY src/OpenPayments.Psp/OpenPayments.Psp.csproj src/OpenPayments.Psp/
COPY src/OpenPayments.Reconciler/OpenPayments.Reconciler.csproj src/OpenPayments.Reconciler/
RUN dotnet restore src/OpenPayments.Reconciler/OpenPayments.Reconciler.csproj
COPY src ./src
RUN dotnet publish src/OpenPayments.Reconciler/OpenPayments.Reconciler.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "OpenPayments.Reconciler.dll"]
