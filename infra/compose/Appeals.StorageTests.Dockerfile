FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/backend/transport/ src/backend/transport/
COPY contracts/events/ contracts/events/
COPY src/backend/services/Appeals/ src/backend/services/Appeals/
COPY tests/integration/Appeals.Persistence.Tests/ tests/integration/Appeals.Persistence.Tests/
RUN dotnet restore tests/integration/Appeals.Persistence.Tests/Appeals.Persistence.Tests.csproj --locked-mode
RUN dotnet build tests/integration/Appeals.Persistence.Tests/Appeals.Persistence.Tests.csproj --no-restore -c Release
ENTRYPOINT ["dotnet", "test", "tests/integration/Appeals.Persistence.Tests/Appeals.Persistence.Tests.csproj", "--no-build", "--no-restore", "-c", "Release", "--logger", "console;verbosity=normal"]
