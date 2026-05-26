import type { NextConfig } from "next";

// Strict-mode by default; the portal renders no client components in v1
// so the strict-mode double-effect isn't a hazard. Reactstrict catches
// silent server-component bugs early.
const config: NextConfig = {
  reactStrictMode: true,
};

export default config;
