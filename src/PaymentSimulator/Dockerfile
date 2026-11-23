FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/PaymentSimulator/PaymentSimulator.csproj .
RUN dotnet restore PaymentSimulator.csproj
COPY src/PaymentSimulator/ .
RUN dotnet publish PaymentSimulator.csproj -c Release --no-restore -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish ./
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PaymentSimulator.dll"]
