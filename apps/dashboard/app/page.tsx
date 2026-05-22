import { redirect } from "next/navigation";

// The dashboard's only entry point in P1 is the providers list. Redirect from
// the root so the URL bar is clean and bookmarks land on the same surface as
// in-app navigation.
export default function Home() {
  redirect("/providers");
}
