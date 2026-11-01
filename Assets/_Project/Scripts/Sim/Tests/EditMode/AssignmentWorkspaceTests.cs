using System;
using NUnit.Framework;
using ACunicombat.Simulation.Core;
using System.Collections;
using Unity.Profiling;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
namespace ACunicombat.Tests.EditMode
{
    public class AssignmentWorkspaceTests
    {
        static void Compare(double[,] matrix,double separation)
        {
            var before=AssignmentBaselineReference.Solve(matrix,separation,out var bt,out var ba,out var bm);
            var after=OneToOneAssignment.Solve(matrix,separation,out var at,out var aa,out var am);
            CollectionAssert.AreEqual(before,after);CollectionAssert.AreEqual(ba,aa);
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(bt),BitConverter.DoubleToInt64Bits(at));
            for(int i=0;i<bm.Length;i++)Assert.AreEqual(BitConverter.DoubleToInt64Bits(bm[i]),BitConverter.DoubleToInt64Bits(am[i]));
        }
        [Test] public void FixedRandomBatchesPreserveEveryAssignmentAmbiguityAndMarginBit()
        {
            var random=new Random(20261027);
            for(int sample=0;sample<1000;sample++)
            {
                var matrix=new double[random.Next(0,18),random.Next(0,24)];
                for(int r=0;r<matrix.GetLength(0);r++)for(int c=0;c<matrix.GetLength(1);c++)
                {
                    int kind=random.Next(12);matrix[r,c]=kind==0?double.NaN:kind==1?double.PositiveInfinity:kind==2?OneToOneAssignment.Rejected:kind==3?10000:random.Next(-20,100)/7.0;
                }
                Compare(matrix,sample%7);
            }
            Compare(new double[16,16],4);
        }
        [Test] public void ConcurrentCallsNeverShareMutableWorkingArrays()
        {
            System.Threading.Tasks.Parallel.For(0,32,index=>{var m=new double[8,12];for(int i=0;i<8;i++)for(int j=0;j<12;j++)m[i,j]=(i-j)*(i-j)+index*.01;Compare(m,4);});
        }
        [UnityTest] public IEnumerator DenseBatchAllocationsAreBoundedByWorkspaceInsteadOfAlternativeRows()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
            var matrix=new double[24,32];for(int r=0;r<24;r++)for(int c=0;c<32;c++)matrix[r,c]=r==c?0:10+Math.Abs(r-c);
            AssignmentBaselineReference.Solve(matrix,4,out _);OneToOneAssignment.Solve(matrix,4,out _);
            // The Mono thread counter returns zero and Editor GC-mode changes are
            // unsupported. Use the same live Unity allocation counter as the scene
            // profiler, with enough identical work to dominate empty-scene noise.
            long before,after;
            using(var recorder=ProfilerRecorder.StartNew(ProfilerCategory.Memory,"GC Allocated In Frame",4))
            {
                Assert.IsTrue(recorder.Valid);for(int i=0;i<5;i++)yield return null;
                for(int i=0;i<100;i++)AssignmentBaselineReference.Solve(matrix,4,out _);
                yield return null;before=recorder.LastValue;
                for(int i=0;i<100;i++)OneToOneAssignment.Solve(matrix,4,out _);
                yield return null;after=recorder.LastValue;
            }
            TestContext.WriteLine($"One hundred identical dense batches, live Unity frame allocation counter: before={before} bytes; after={after} bytes.");
            Assert.Greater(before,0,"Allocation instrumentation must produce a meaningful measurement.");
            Assert.Less(after,before/5,"This guards against restoring per-alternative/per-row arrays.");
            yield return new ExitPlayMode();
        }
    }
}
