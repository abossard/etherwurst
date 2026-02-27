FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["HazMeBeenScammed.Web/HazMeBeenScammed.Web.csproj", "HazMeBeenScammed.Web/"]
COPY ["HazMeBeenScammed.Core/HazMeBeenScammed.Core.csproj", "HazMeBeenScammed.Core/"]
COPY ["HazMeBeenScammed.ServiceDefaults/HazMeBeenScammed.ServiceDefaults.csproj", "HazMeBeenScammed.ServiceDefaults/"]
RUN dotnet restore "HazMeBeenScammed.Web/HazMeBeenScammed.Web.csproj"
COPY . .
RUN dotnet publish "HazMeBeenScammed.Web/HazMeBeenScammed.Web.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "HazMeBeenScammed.Web.dll"]
