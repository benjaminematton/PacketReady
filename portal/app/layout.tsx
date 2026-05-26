import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "PacketReady — provider intake",
  description: "Provider credentialing intake portal.",
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
