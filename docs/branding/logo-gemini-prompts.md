# Viegard Logo / Icon: Gemini Image Prompts

Prompts for generating the Viegard raven sentinel logo with Gemini.  The mark is used as a repository avatar (Forgejo + GitHub), an application icon, and a favicon, so it must stay legible at 16 to 32 pixels.

## Primary prompt (icon / repo avatar)

> A minimalist flat vector logo of a vigilant raven sentinel, side profile, perched upright with alert posture, head turned slightly forward as if watching.  The raven's eye is a single sharp geometric accent in amber-gold.  Bold, clean silhouette built from a few smooth geometric curves, no fine feather detail.  Two-color design: near-black charcoal raven (#1B1F23) on a transparent background, amber-gold eye accent (#F0A830).  Centered in a square composition with generous negative space.  Style: modern flat iconography, sharp edges, no gradients, no shadows, no text, no border.  Must remain recognizable when scaled down to a 16-pixel favicon.

## Variant: shield-badge version (application icon)

> A minimalist flat vector emblem: a vigilant raven in side profile perched at the top of a subtle rounded shield outline, suggesting a sentinel guarding a keep.  The raven is a solid near-black charcoal silhouette (#1B1F23); the shield is a thin single-weight outline in the same charcoal; the raven's eye is a single amber-gold dot (#F0A830).  Flat iconography, two colors only, transparent background, square composition, no gradients, no shadows, no text.  Designed to read clearly as a small application icon.

## Variant: wordmark lockup (README banner)

> A horizontal logo lockup: on the left, a minimalist flat vector raven sentinel in side profile with an amber-gold eye accent; on the right, the word "VIEGARD" in a modern geometric sans-serif, all caps, charcoal near-black (#1B1F23), with wide letter-spacing.  Flat design, two colors (charcoal #1B1F23 and amber-gold #F0A830), transparent background, no gradients, no shadows, no tagline.  Balanced spacing between mark and wordmark.

## Usage notes

- Request PNG with transparency at 1024x1024 (icon) and export downscaled sizes (512, 256, 64, 32, 16) for favicons and app icons.
- For dark UI surfaces, invert the charcoal to off-white (#E8EAED) and keep the amber-gold eye.
- Keep generated assets in `docs/branding/`; commit final selections only (binary assets are tracked but iterations should not bloat the repository).
