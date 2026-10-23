FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/backend/services/Checking/ src/backend/services/Checking/
COPY tests/integration/Checking.Persistence.Tests/ tests/integration/Checking.Persistence.Tests/
COPY tests/fixtures/PreparedFixtureFiles.cs tests/fixtures/PreparedFixtureFiles.cs
COPY infra/fixtures/ infra/fixtures/
RUN dotnet restore tests/integration/Checking.Persistence.Tests/Checking.Persistence.Tests.csproj --locked-mode
RUN dotnet build tests/integration/Checking.Persistence.Tests/Checking.Persistence.Tests.csproj --no-restore -c Release
ENTRYPOINT ["dotnet", "test", "tests/integration/Checking.Persistence.Tests/Checking.Persistence.Tests.csproj", "--no-build", "--no-restore", "-c", "Release", "--logger", "console;verbosity=normal"]
