"use client";

import { useState } from "react";
import { Document, Page, pdfjs } from "react-pdf";
import "react-pdf/dist/Page/AnnotationLayer.css";
import "react-pdf/dist/Page/TextLayer.css";
import type { BoundingBox } from "@/lib/types";
import { cn } from "@/lib/utils";

// react-pdf needs the pdf.js worker. Resolved against the module URL so
// Turbopack bundles it; pinning the worker version to react-pdf's bundled
// pdfjs-dist (rather than a CDN URL) avoids the "API version 5.x, worker
// version 4.x" startup error.
pdfjs.GlobalWorkerOptions.workerSrc = new URL(
  "pdfjs-dist/build/pdf.worker.min.mjs",
  import.meta.url,
).toString();

type Props = {
  /**
   * Absolute URL to the PDF blob. The parent server component builds this from
   * `API_BASE_URL` + `/api/documents/{id}/blob` — the dashboard's `API_BASE_URL`
   * stays server-only, no `NEXT_PUBLIC_` leak required.
   */
  blobUrl: string;
  /** 1-indexed page number (matches `Citation.Page` from the API). */
  page: number;
  /** Normalized 0..1 coordinates. Null means render the page without a highlight. */
  bbox: BoundingBox | null;
  /**
   * Render width in pixels. Heights derive from the PDF's page aspect ratio.
   * Default sized for the IssueCard side-panel column on a 13" laptop demo.
   */
  width?: number;
  className?: string;
};

/**
 * Renders a single PDF page with an optional bbox overlay. Used from the
 * citation drill-in tab — Issue → Citation → (documentId, page, bbox) →
 * this component, all inputs typed from `lib/types`.
 *
 * <para><b>No zoom / pan in P6.</b> A static page with a static highlight is
 * enough for the 5-minute demo. Zoom adds two more UI states to polish; cut.</para>
 *
 * <para>Dimensions for the bbox overlay are captured from
 * <c>onRenderSuccess</c> rather than the Page's nominal viewport — react-pdf
 * scales fonts and the bbox needs to follow what's actually on screen.</para>
 */
export function PdfPreview({ blobUrl, page, bbox, width = 480, className }: Props) {
  const [rendered, setRendered] = useState<{ w: number; h: number } | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  return (
    <div className={cn("inline-block", className)}>
      <div className="relative inline-block rounded border border-zinc-200 bg-zinc-50 dark:border-zinc-800 dark:bg-zinc-900">
        <Document
          file={blobUrl}
          loading={<Skeleton width={width} />}
          error={
            <ErrorState message={loadError ?? "Failed to load PDF."} width={width} />
          }
          onLoadError={(err) => setLoadError(err.message)}
        >
          <Page
            pageNumber={page}
            width={width}
            renderTextLayer={false}
            renderAnnotationLayer={false}
            onRenderSuccess={(p) =>
              setRendered({ w: p.width, h: p.height })
            }
            loading={<Skeleton width={width} />}
          />
        </Document>
        {bbox && rendered && (
          <BboxOverlay bbox={bbox} renderedWidth={rendered.w} renderedHeight={rendered.h} />
        )}
      </div>
    </div>
  );
}

function BboxOverlay({
  bbox,
  renderedWidth,
  renderedHeight,
}: {
  bbox: BoundingBox;
  renderedWidth: number;
  renderedHeight: number;
}) {
  // bbox is normalized 0..1 top-left-origin (matches Domain/Scoring/Citation.cs).
  const left = bbox.x1 * renderedWidth;
  const top = bbox.y1 * renderedHeight;
  const w = (bbox.x2 - bbox.x1) * renderedWidth;
  const h = (bbox.y2 - bbox.y1) * renderedHeight;

  return (
    <div
      // `pointer-events-none` so clicks fall through to text-layer if it's ever
      // re-enabled; the overlay is decorative, never interactive.
      className="pointer-events-none absolute rounded-sm border-2 border-amber-500/80 bg-amber-300/20 ring-1 ring-amber-400/40"
      style={{ left, top, width: w, height: h }}
      aria-hidden="true"
    />
  );
}

function Skeleton({ width }: { width: number }) {
  // Reserve approximate-letter page space at the same width so loading doesn't
  // cause layout shift mid-demo. 11/8.5 ≈ letter aspect ratio.
  const height = width * (11 / 8.5);
  return (
    <div
      className="animate-pulse bg-zinc-100 dark:bg-zinc-900"
      style={{ width, height }}
      aria-label="Loading PDF preview"
    />
  );
}

function ErrorState({ message, width }: { message: string; width: number }) {
  return (
    <div
      style={{ width }}
      className="flex flex-col items-center justify-center gap-1 px-6 py-12 text-center"
    >
      <p className="text-sm font-medium text-rose-700 dark:text-rose-300">
        Couldn&apos;t load the source PDF.
      </p>
      <p className="text-xs text-zinc-500 dark:text-zinc-400">{message}</p>
    </div>
  );
}
