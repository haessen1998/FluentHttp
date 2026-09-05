# ReadableHttp.Auth

ReadableHttp.Auth provides authentication helpers for ReadableHttp-based clients and tools.

The package focuses on OAuth2 helper flows, PKCE generation, localhost callback handling, token exchange, and a lightweight token cache. The core `ReadableHttp` package already contains auth metadata models; this package adds helper services around those models.

## Install

```shell
dotnet add package ReadableHttp.Auth --version 2.1.0
```

## PKCE

```csharp
using ReadableHttp.Auth;

var pkce = OAuth2Pkce.Create();

Console.WriteLine(pkce.CodeVerifier);
Console.WriteLine(pkce.CodeChallenge);
Console.WriteLine(pkce.Method);
```

## Authorization URL

```csharp
using ReadableHttp;
using ReadableHttp.Auth;

var request = OAuth2AuthorizationRequest.FromOptions(
    new ReadableOAuth2Options
    {
        AuthorizationUrl = "https://identity.example.com/oauth/authorize",
        ClientId = "client-id",
        Scopes = ["openid", "profile", "email"],
        UsePkce = true
    },
    redirectUri: "http://127.0.0.1:7890/callback",
    state: "state");

Console.WriteLine(request.ToUri());
```

## Token Cache

```csharp
using ReadableHttp.Auth;

var cache = new OAuth2TokenCache();
cache.Set("default", new OAuth2TokenResponse
{
    AccessToken = "access-token",
    ExpiresIn = 3600
});

var token = cache.GetUsable("default");
```
