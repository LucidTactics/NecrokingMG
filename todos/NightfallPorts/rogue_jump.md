This shows how the jump was implemented in NightfallRogue.

Note SlideMoveTowards moves the unit horizontally at its speed towards the destination. There is some other book keeping here that can ignore.

```
   public class JumpAction {
      public Vector2 dest;
      public Vector2 form_dest;
      public float time_cost;

      public float arch_height;

      public float tc_orig;

      public int state;

      public float time_wait;
   }
```

```
   public static OrderStatus ProcessJumpAction(UnitHandle uni, float dt, JumpAction cur_jump) {
      switch (cur_jump.state) {
         case 0: {
            uni.animation_renderer.PreJump();
            uni.animation_renderer.Face(cur_jump.dest - uni.fpos);
            cur_jump.state = 1;
         }
            return ProcessJumpAction(uni, dt, cur_jump);
         case 1: {
            cur_jump.time_wait += dt;

            if (uni.animation_renderer.cur_clip.state == Standup) {
               return true;
            }

            // Debug.Log($"Waited {cur_jump.time_wait}, jump dur: {cur_jump.time_cost}");
            cur_jump.state++;
            uni.NowPosLock(cur_jump.dest.Round(), uni.sidePosFilter);
            cur_jump.form_dest = uni.GetDisorderPos(uni.tile.pos);
         }
            return ProcessJumpAction(uni, dt, cur_jump);
         case 2: {
            if (SlideMoveTowards(uni, cur_jump.form_dest, cur_jump.time_cost, dt)) {
               dt -= cur_jump.time_cost;
               uni.SetFlyHeightTmp(0);
               cur_jump.state++;
               uni.animation_renderer.PostJunp();
               uni.animation_renderer.cur_animation_state = None;
               return ProcessJumpAction(uni, dt, cur_jump);
            }

            cur_jump.time_cost -= dt;
            var h = Asd.ArchProgress(cur_jump.time_cost, cur_jump.tc_orig);

            // Debug.Log($"{new{h, cur_jump.arch_height, cur_jump.tc_orig, cur_jump.time_cost}}");

            var jh = cur_jump.arch_height * h;
            uni.SetFlyHeightTmp(jh);
         }
            return true;
         case 3: {
         }
            return false;
         default:
            break;
      }

      return false;
   }
   ```

   ```
   public void PostJunp() {
      PlayOnce(StandupR, 1.5f);
      // render_wait = 0;
   }

   public void PreJump() {
      var a = animation_dict[Standup];

      PlayOnce(Standup, 1.5f);

      cur_clip.i = a.sprites.Length / 2;

      cur_animation_state = AnimationState.Fall;
   }
   ```