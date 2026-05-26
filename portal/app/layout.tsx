import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "PacketReady — provider intake",
  description: "Provider credentialing intake portal.",
  // Magic-link tokens ride in the URL path. `noindex` keeps a forwarded
  // link out of search results; `no-referrer` keeps the token out of
  // Referer headers on any outbound navigation or third-party asset.
  robots: { index: false, follow: false },
  referrer: "no-referrer",
};

export default function RootLayout({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body className="antialiased">{children}</body>
    </html>
  );
}
