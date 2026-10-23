FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/backend/services/Results/ src/backend/services/Results/
COPY tests/integration/Results.Persistence.Tests/ tests/integration/Results.Persistence.Tests/
COPY infra/fixtures/ infra/fixtures/
RUN dotnet restore tests/integration/Results.Persistence.Tests/Results.Persistence.Tests.csproj --locked-mode
RUN dotnet build tests/integration/Results.Persistence.Tests/Results.Persistence.Tests.csproj --no-restore -c Release
ENTRYPOINT ["dotnet", "test", "tests/integration/Results.Persistence.Tests/Results.Persistence.Tests.csproj", "--no-build", "--no-restore", "-c", "Release", "--logger", "console;verbosity=normal"]
