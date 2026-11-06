FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props JsonMorph.sln ./
COPY src/ src/
RUN dotnet publish src/JsonMorph.Api -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    JsonMorph__DataRoot=/data
RUN mkdir -p /data && chown app /data
VOLUME /data
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "JsonMorph.Api.dll"]
