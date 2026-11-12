FROM mcr.microsoft.com/dotnet/sdk:8.0

WORKDIR /workspace
COPY . .
RUN dotnet build MuNTrade.sln -c Release && chmod +x ./demo.sh

ENTRYPOINT ["./demo.sh"]
