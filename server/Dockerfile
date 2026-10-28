FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY JournalStack.Server/ JournalStack.Server/
RUN dotnet publish JournalStack.Server -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "JournalStack.Server.dll"]
