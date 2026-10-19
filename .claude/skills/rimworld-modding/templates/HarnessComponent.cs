// Template: in-game integration harness driver. Rename the namespace/IDs, keep the structure.
// Lives in a separate test-only mod that depends on the mod under test. See references/harness.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace MyModTest;

public class HarnessComponent(Game game) : GameComponent
{
    private static string Dir => GenFilePaths.SaveDataFolderPath;
    private static string Marker => Path.Combine(Dir, "harness_marker.txt");
    private static string Results => Path.Combine(Dir, "harness_results.txt");

    private static int fails;
    private static bool finished;      // Application.Quit is async: stop everything once set
    private static int stage;          // tick-driven scenario stage
    private static int stageStart;
    private static int shotStage;      // frame-driven screenshot / UI stage
    private static int shotFrame;
    private bool started;
    private bool verifyPending;

    // ------------------------------------------------------------------ plumbing

    public static void Check(bool ok, string what)
    {
        if (!ok)
        {
            fails++;
        }

        var line = $"[HARNESS] {(ok ? "PASS" : "FAIL")} {what}";
        Log.Message(line);
        File.AppendAllText(Results, line + "\n");
    }

    private static void Finish()
    {
        finished = true;
        Check(ErrorWatch.Captured.Count == 0, $"no errors from the mod during the run ({ErrorWatch.Captured.Count})");
        foreach (var e in ErrorWatch.Captured.Distinct().Take(10))
        {
            File.AppendAllText(Results, "[HARNESS]   logged: " + e.Replace("\n", " | ") + "\n");
        }

        File.AppendAllText(Results, $"[HARNESS] DONE fails={fails}\n");
        if (File.Exists(Marker))
        {
            File.Delete(Marker);
        }

        Application.Quit();
    }

    private static void Next()
    {
        stage++;
        stageStart = Find.TickManager.TicksGame;
    }

    private static int Elapsed => Find.TickManager.TicksGame - stageStart;

    /// <summary>Keep a test worker on task: full needs, no breaks, re-issue the job if it drifted.</summary>
    public static void KeepWorking(Pawn worker, JobDef expected, Func<Job> makeJob, int elapsed)
    {
        foreach (var need in worker.needs?.AllNeeds ?? [])
        {
            need.CurLevelPercentage = 1f;
        }

        if (worker.InMentalState)
        {
            worker.MentalState.RecoverFromState();
        }

        if (elapsed > 0 && elapsed % 500 == 0 && worker.CurJobDef != expected && makeJob() is { } job)
        {
            worker.jobs.StartJob(job, JobCondition.InterruptForced);
        }
    }

    // ------------------------------------------------------------------ lifecycle

    public override void LoadedGame()
    {
        if (File.Exists(Marker))
        {
            verifyPending = true;
        }
    }

    public override void GameComponentTick()
    {
        if (finished)
        {
            return;
        }

        try
        {
            if (verifyPending)
            {
                verifyPending = false;
                VerifyAfterReload();
                shotStage = 1;
                shotFrame = Time.frameCount + 30;
                return;
            }

            if (!started)
            {
                if (File.Exists(Marker) || Find.TickManager.TicksGame < 120)
                {
                    return;
                }

                started = true;
                File.WriteAllText(Results, "");
                SynchronousChecks();
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
                Next();
                return;
            }

            ScenarioTick();
        }
        catch (Exception e)
        {
            Check(false, "exception: " + e);
            Finish();
        }
    }

    // ------------------------------------------------------------------ your tests

    private static void SynchronousChecks()
    {
        var map = Find.CurrentMap;
        Check(DefDatabase<ThingDef>.GetNamedSilentFail("MyMod_Thing") != null, "defs loaded");
        // Spawn fresh fixtures here; never rely on what quicktest generated.
    }

    // Example of a job-based stage: start a real job via the work giver, then wait with a timeout.
    private static Pawn worker;
    private static Thing target;

    private static void ScenarioTick()
    {
        switch (stage)
        {
            case 1:
                // e.g. worker = map.mapPawns.FreeColonistsSpawned.First(c => !c.WorkTypeIsDisabled(WorkTypeDefOf.Doctor));
                Next();
                break;
            case 2:
                // KeepWorking(worker, MyDefOf.MyJob, () => giver.HasJobOnThing(worker, target, true) ? giver.JobOnThing(worker, target, true) : null, Elapsed);
                // if (done) { Check(...); Next(); } else if (Elapsed > 8000) { Check(false, "timed out: ..."); Next(); }
                Next();
                break;
            default:
                SaveAndReload();
                break;
        }
    }

    private static void SaveAndReload()
    {
        stage = -1; // stop the scenario loop; the reloaded game continues in LoadedGame/VerifyAfterReload
        File.WriteAllText(Marker, "ids,to,refind");
        GameDataSaveLoader.SaveGame("harness");
        Check(true, "saved");
        GameDataSaveLoader.LoadGame("harness");
    }

    private static void VerifyAfterReload()
    {
        // Check invariants, not specific objects earlier scenarios may have consumed.
    }

    // ------------------------------------------------------------------ screenshots / UI smoke (per frame)

    public override void GameComponentUpdate()
    {
        if (finished || shotStage == 0 || Time.frameCount < shotFrame)
        {
            return;
        }

        var dir = Path.Combine(Dir, "shots");
        Directory.CreateDirectory(dir);
        switch (shotStage)
        {
            case 1:
                // Find.WindowStack.Add(new MyDialog());
                break;
            case 2:
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "1_dialog.png"));
                break;
            default:
                Finish();
                return;
        }

        shotStage++;
        shotFrame = Time.frameCount + 30;
    }
}
