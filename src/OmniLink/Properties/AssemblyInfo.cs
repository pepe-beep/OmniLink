using System.Runtime.CompilerServices;

// ให้ test project เข้าถึง internal helper (เช่น OmniPaths.DiscoverRoot) ได้เพื่อการทดสอบ
[assembly: InternalsVisibleTo("OmniLink.Tests")]
