using System;

namespace ACunicombat.Simulation.Core
{
    /// <summary>Whole-batch one-to-one assignment. Ambiguous alternatives remain unassigned.</summary>
    public static class OneToOneAssignment
    {
        public const double Rejected = 1e12;
        private const double Unmatched = 10000;

        public static int[] Solve(double[,] costs, double separation, out double total)
            =>Solve(costs,separation,out total,out _);

        public static int[] Solve(double[,] costs, double separation, out double total,out bool[] ambiguous)
            =>Solve(costs,separation,out total,out ambiguous,out _);

        public static int[] Solve(double[,] costs, double separation, out double total,out bool[] ambiguous,out double[] margins)
        {
            int rows=costs.GetLength(0);
            var workspace=new Workspace(rows,costs.GetLength(1)+rows);
            var best=new int[rows];var other=new int[rows];
            Optimize(costs,-1,-1,workspace,best,out total);
            var result=(int[])best.Clone();
            ambiguous=new bool[best.Length];
            margins=new double[best.Length];
            for(int i=0;i<margins.Length;i++)margins[i]=double.PositiveInfinity;
            for(int row=0;row<best.Length;row++)
            {
                if(best[row]<0)continue;
                Optimize(costs,row,best[row],workspace,other,out double alternative);
                margins[row]=alternative-total;
                if(alternative-total<separation)
                    for(int i=0;i<best.Length;i++)if(other[i]!=best[i]){result[i]=-1;ambiguous[i]=true;}
            }
            return result;
        }

        // A private workspace per Solve keeps concurrent/reentrant calls isolated.
        // Reuse changes storage only: tie order, arithmetic and ambiguity margins
        // intentionally remain byte-for-byte equivalent to the original algorithm.
        private sealed class Workspace
        {
            public readonly double[] U,V,Min;
            public readonly int[] P,Way;
            public readonly bool[] Used;
            public Workspace(int rows,int columns)
            {U=new double[rows+1];V=new double[columns+1];Min=new double[columns+1];P=new int[columns+1];Way=new int[columns+1];Used=new bool[columns+1];}
        }
        private static void Optimize(double[,] costs,int forbiddenRow,int forbiddenColumn,Workspace workspace,int[] result,out double total)
        {
            int n=costs.GetLength(0),real=costs.GetLength(1),m=real+n;
            var u=workspace.U;var v=workspace.V;var p=workspace.P;var way=workspace.Way;var min=workspace.Min;var used=workspace.Used;
            Array.Clear(u,0,u.Length);Array.Clear(v,0,v.Length);Array.Clear(p,0,p.Length);Array.Clear(way,0,way.Length);
            for(int i=1;i<=n;i++)
            {
                p[0]=i;int j0=0;min[0]=0;Array.Clear(used,0,used.Length);
                for(int j=1;j<=m;j++)min[j]=Rejected;
                do
                {
                    used[j0]=true;int i0=p[j0],j1=0;double delta=Rejected;
                    for(int j=1;j<=m;j++)if(!used[j])
                    {
                        double c=j<=real?costs[i0-1,j-1]:Unmatched;
                        if(i0-1==forbiddenRow&&j-1==forbiddenColumn)c=Rejected;
                        if(!double.IsFinite(c))c=Rejected;
                        double cur=c-u[i0]-v[j];
                        if(cur<min[j]){min[j]=cur;way[j]=j0;}
                        if(min[j]<delta){delta=min[j];j1=j;}
                    }
                    for(int j=0;j<=m;j++)if(used[j]){u[p[j]]+=delta;v[j]-=delta;}else min[j]-=delta;
                    j0=j1;
                }while(p[j0]!=0);
                do{int j1=way[j0];p[j0]=p[j1];j0=j1;}while(j0!=0);
            }
            for(int i=0;i<n;i++)result[i]=-1;
            for(int j=1;j<=real;j++)if(p[j]>0&&costs[p[j]-1,j-1]<Unmatched&&!(p[j]-1==forbiddenRow&&j-1==forbiddenColumn))result[p[j]-1]=j-1;
            total=0;for(int i=0;i<n;i++)total+=result[i]<0?Unmatched:costs[i,result[i]];
        }
    }
}
