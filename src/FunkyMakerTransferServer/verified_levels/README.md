# Verified Levels Folder

This folder contains official verified levels that will be automatically served by the server.

## How to Add Levels:

1. Place `.FNMM` files in this folder
2. Commit and push to GitHub
3. Render will automatically redeploy and scan these files

## File Format:

- **Extension**: `.FNMM` (uppercase)
- **Naming**: Use descriptive names (e.g., `Tutorial.FNMM`, `Hard_Mode.FNMM`)
- **Size**: If files are larger than 25MB, use Git LFS (see docs/UPLOAD_LARGE_LEVELS_GUIDE.md)

## Server Behavior:

The server will:
- Scan this folder on startup
- Calculate SHA256 hash for each file
- Generate level ID from filename (lowercase, spaces → underscores)
- Serve files via `/api/levels/{id}/download` endpoint

## Example:

File: `Tutorial.FNMM`
- ID: `tutorial`
- Download URL: `https://funkymakerserver.onrender.com/api/levels/tutorial/download`

---

**Note:** For files >25MB, setup Git LFS:
```bash
git lfs install
git lfs track "*.FNMM"
git add .gitattributes
git commit -m "Configure Git LFS"
```
