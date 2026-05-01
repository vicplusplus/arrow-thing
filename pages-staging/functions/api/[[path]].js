// Cloudflare Pages Function — staging only.
//
// Proxies /api/* on staging.arrow-thing.com to api-staging.arrow-thing.com so
// the browser sees authenticated API calls as same-origin. Without this, the
// API's HttpOnly cookies (Domain=.staging.arrow-thing.com from
// docker-compose.yml) are silently rejected when set on responses from
// api-staging.arrow-thing.com — that hostname is a sibling subdomain, not a
// child of staging.arrow-thing.com, so RFC 6265's domain-suffix check fails.
// The Cloudflare free tier's Universal SSL only covers one wildcard level,
// which rules out the obvious "rename to api.staging.arrow-thing.com" fix.
//
// This Function only exists in the staging Pages project — the prod project
// (arrow-thing) doesn't bundle it, so prod's arrow-thing.com → api.arrow-thing.com
// flow (which already works because both share .arrow-thing.com) is untouched.
//
// WebSockets (/ws/coop/*) are not routed through this Function; CoopClient
// connects directly to wss://api-staging.arrow-thing.com with the JWT in the
// query string, which doesn't depend on cookie scope.

export async function onRequest(context) {
    const { request } = context;
    const url = new URL(request.url);
    const target = new URL(
        url.pathname + url.search,
        'https://api-staging.arrow-thing.com'
    );
    // new Request(target, request) inherits method/headers/body but rewrites
    // the URL; fetch() then sets Host to the target host so nginx's
    // $host-keyed upstream map routes to the api-staging container.
    return fetch(new Request(target, request));
}
