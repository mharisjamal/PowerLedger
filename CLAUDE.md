# PowerLedger — Claude Code notes

## Premium Apple-Inspired Liquid Glass UI

For all frontend UI work:

- Use frontend-design for overall visual composition.
- Use LiquidGlassSkill for Liquid Glass implementation.
- Use the motion skill for animation and interaction.
- Use UI skills for UX, animation, accessibility and design tokens.

Design target: an Apple-inspired Liquid Glass aesthetic. The UI should feel premium, minimal, spatial, translucent,
refined, responsive, depth-aware, tactile, fluid and highly polished.

Do NOT create generic AI/SaaS dashboard aesthetics. Do NOT turn every component into a glass card. Liquid Glass is
primarily for navigation, floating controls, toolbars, contextual controls, overlays, menus and selected or
high-priority controls. Keep the main content layer visually clear.

Where a look has an approved design spec in docs/superpowers/specs (for example the Midnight look, approved
2026-09-24), that spec's layout and palette win; these skills supply the tokens, glass materials, motion and
accessibility craft within it.

### Liquid Glass implementation rules

Build a reusable Liquid Glass design system from design tokens, never arbitrary values: glass opacity, blur,
saturation, brightness, border highlight, inner highlight, shadow, elevation, radius, reflection, transition and
spring parameters. Glass supports light and dark mode, adaptive contrast, and hover, active, pressed, focused,
disabled and selected states. Avoid excessive blur or transparency, glass everywhere, stacked glass-on-glass,
unreadable text, excessive gradients or shadows, fake reflections and visual noise. In WPF, blur (BlurEffect) is
GPU-expensive: use it only on small surfaces (tooltips, menus, the top bar), never on page content.

### Motion rules

Motion communicates spatial relationships. Prefer spring physics, transform and opacity animations, layout and
shared-element transitions, gesture-driven and scroll-linked animation, subtle parallax, morphing controls and
contextual transitions. Avoid unnecessary animation, everything fading in or sliding from the bottom, linear easing
everywhere, excessive bouncing, huge scale effects, or animation that delays usability.

Interaction examples: button hover → subtle illumination, press → slight compression, release → spring back; card
hover → subtle elevation; modal open → originates from its trigger; sidebar open → fluid spatial expansion; tab active
indicator → morphs between positions; toolbar state changes → morph rather than replace.

### Accessibility

Keyboard navigation, visible focus, WCAG-conscious contrast, reduced motion, responsive layouts and screen readers
where applicable. Respect the system's reduced-motion setting (WPF: SystemParameters.ClientAreaAnimation): replace
large spatial animations with subtle opacity, colour or state transitions rather than removing function.

### Performance

Glass and animation must be performance-conscious: avoid unnecessary blur layers, nested blur, large continuously
animated filters, expensive shadow animation and layout-triggering animation. Prefer transform, opacity,
compositor-friendly animation and GPU-friendly effects. Check for animation jank before calling a UI complete.
