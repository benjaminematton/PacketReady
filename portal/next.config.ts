import type { NextConfig } from "next";

// Strict-mode by default; the portal renders no client components in v1
// so the strict-mode double-effect isn't a hazard. Reactstrict catches
// silent server-component bugs early.
//
// Security headers below are a defensive baseline. The portal is a
// magic-link-keyed credentialing surface — even though today's data is
// synthetic, the path is real and the headers cost nothing.
const SECURITY_HEADERS = [
  // No third-party embed, no `unsafe-eval`. Next.js inlines a small
  // amount of bootstrap script + style; `'unsafe-inline'` is required
  // for the App Router runtime today. Tighten with a nonce-based CSP
  // once a real session middleware lands.
  {
    key: "Content-Security-Policy",
    value: [
      "default-src 'self'",
      "script-src 'self' 'unsafe-inline'",
      "style-src 'self' 'unsafe-inline'",
      "img-src 'self' data:",
      "font-src 'self' data:",
      "connect-src 'self'",
      "frame-ancestors 'none'",
      "base-uri 'self'",
      "form-action 'self'",
    ].join("; "),
  },
  // Belt-and-braces with the CSP `frame-ancestors` above for legacy UAs.
  { key: "X-Frame-Options", value: "DENY" },
  { key: "X-Content-Type-Options", value: "nosniff" },
  // Reinforces the `referrer: 'no-referrer'` in layout metadata at the
  // network edge so a missing meta tag (regen, theme change) doesn't
  // silently start leaking the token in `Referer`.
  { key: "Referrer-Policy", value: "no-referrer" },
  // Tight permissions — the portal needs none of these.
  {
    key: "Permissions-Policy",
    value: "camera=(), microphone=(), geolocation=(), interest-cohort=()",
  },
  // 1 year HSTS with preload. Safe because the portal is always served
  // over TLS in production; dev runs on plain HTTP and ignores HSTS.
  {
    key: "Strict-Transport-Security",
    value: "max-age=31536000; includeSubDomains; preload",
  },
];

const config: NextConfig = {
  reactStrictMode: true,
  async headers() {
    return [{ source: "/:path*", headers: SECURITY_HEADERS }];
  },
};

export default config;
