import Link from "next/link";

export default function AdminLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <div>
      <h1 className="text-2xl">Admin view</h1>

      <nav className="flex gap-4 py-4">
        <Link
          href="/admin/products"
          className="hover:underline text-muted-foreground"
        >
          Products
        </Link>
      </nav>

      {children}
    </div>
  );
}
