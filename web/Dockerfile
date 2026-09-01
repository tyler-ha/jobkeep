# The front end's DEV server, in a container. Used by compose.yaml at the repo
# root.
#
# Deliberately not a production build behind nginx. Hot reload is the reason to
# run the front end locally at all, and a container that replaced Vite with a
# static bundle would be strictly worse than `npm run dev` -- so nobody would use
# it, and the compose file would rot. A production image belongs with the deploy
# (Phase 10), where there is somewhere to push it.

FROM node:24-alpine
WORKDIR /app

# Manifest and lockfile first, so the install layer survives a source edit.
# `npm ci` and not `npm install`: it installs exactly the lockfile, and it fails
# loudly if the two have drifted rather than quietly resolving something else.
COPY package.json package-lock.json ./
RUN npm ci

# Copied so the image can run on its own (`docker run`), even though compose
# then shadows /app with a bind mount for hot reload. The node_modules installed
# above survive that, via the anonymous volume compose declares -- see the
# comment there.
COPY . .

EXPOSE 5173

# --host, because a dev server bound to 127.0.0.1 inside a container is reachable
# from nothing outside it. The port itself is pinned in vite.config.ts with
# strictPort, so a clash fails at startup instead of silently moving to 5174 and
# failing every request on CORS.
CMD ["npm", "run", "dev", "--", "--host", "0.0.0.0"]
