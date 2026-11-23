FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /repo
COPY src/backend/transport/ src/backend/transport/
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY contracts/events/ contracts/events/
COPY src/backend/services/Results/ src/backend/services/Results/
RUN dotnet restore src/backend/services/Results/Results.Api/Results.Api.csproj --locked-mode
RUN dotnet publish src/backend/services/Results/Results.Api/Results.Api.csproj --no-restore -c Release -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:8af87eba5e7cdafb1514d7388be287365da97c132509e84dfe252ffd552398ca
WORKDIR /app
COPY --from=build /app ./
COPY infra/compose/results-entrypoint.sh /app/entrypoint.sh
ENTRYPOINT ["sh", "/app/entrypoint.sh"]
