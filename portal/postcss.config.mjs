// Tailwind v4 ships its PostCSS plugin separately; the v3 `tailwindcss`
// PostCSS entry point is gone. Single plugin, no autoprefixer (v4
// bundles a Lightning CSS-based prefixer internally).
const config = {
  plugins: {
    "@tailwindcss/postcss": {},
  },
};

export default config;
