Currently a lot of gameplay behaviour has sneaked into the animation functions, so the gameplay events fires at some specific animation point depending on the animation timings etc.

We should try to avoid that, so look through the Game1.Animation.cs file for example for gameplay behaviours and decouple those.

The first step is to figure out a system where we can still have nice timings together with animations, without dependening on the animation speficifics here.

Ideally we should speed up slow animations to match higher cast speeds etc.