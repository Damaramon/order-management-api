FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet restore --locked-mode
RUN dotnet test --configuration Release --no-restore
RUN dotnet publish src/OrderManagement.Api --configuration Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
ENV Database__Path=/app/data/orders.db
RUN mkdir -p /app/data && chown -R app:app /app/data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "OrderManagement.Api.dll"]
