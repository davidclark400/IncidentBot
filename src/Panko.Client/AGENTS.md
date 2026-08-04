# Frontend Guidance

This client is a React + TypeScript app built with Vite, Tailwind CSS, and shadcn/ui.

## Styling

- Prefer Tailwind utility classes for layout, spacing, color, typography, and responsiveness.
- Keep custom CSS to a minimum. Use `src/index.css` only for global base styles and Tailwind imports.
- Avoid adding new stylesheet files unless a utility-based approach is clearly impractical.

## Components

- Prefer shadcn/ui components for common UI patterns such as buttons, dialogs, dropdowns, forms, cards, tables, and inputs.
- Add reusable shadcn components under `src/components/ui`.
- Compose screens from small, reusable React components rather than large page-only components.

## Conventions

- Keep the design system consistent by reusing Tailwind tokens and shadcn component variants.
- When adding a new UI pattern, first check whether a shadcn component already covers it.
- If you need a new shadcn component, add it through the normal shadcn workflow rather than hand-rolling an equivalent.

## Build And Verification

- Run `npm run build` after UI or styling changes.
- Keep the client compatible with the existing Vite setup and Tailwind integration.
