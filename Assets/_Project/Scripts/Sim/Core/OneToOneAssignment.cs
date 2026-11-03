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

        // Rows only interact through shared candidate columns (cost below Unmatched), so the
        // batch is solved per connected component. Each row's alternative (its chosen pair
        // forbidden) re-solves only its own component instead of the whole batch: the other
        // components' assignments and costs are unaffected. Exact ties may break differently.
        public static int[] Solve(double[,] costs, double separation, out double total,out bool[] ambiguous,out double[] margins)
            =>Solve(costs,costs.GetLength(0),costs.GetLength(1),separation,out total,out ambiguous,out margins);

        /// <summary>Solves the top-left rows x columns block of a (possibly larger, reused) cost buffer.</summary>
        public static int[] Solve(double[,] costs,int rows,int columns, double separation, out double total,out bool[] ambiguous,out double[] margins)
        {
            if((uint)rows>(uint)costs.GetLength(0)||(uint)columns>(uint)costs.GetLength(1))throw new ArgumentOutOfRangeException(nameof(rows));
            var result=new int[rows];
            ambiguous=new bool[rows];
            margins=new double[rows];
            for(int i=0;i<rows;i++){result[i]=-1;margins[i]=double.PositiveInfinity;}
            var s=scratch??=new Scratch();
            s.Ensure(rows,columns);
            // Union-find over rows; a column links every row that can take it.
            var parent=s.Parent;var columnOwner=s.ColumnOwner;
            for(int i=0;i<rows;i++)parent[i]=i;
            for(int j=0;j<columns;j++)columnOwner[j]=-1;
            for(int i=0;i<rows;i++)for(int j=0;j<columns;j++)
            {
                if(!(costs[i,j]<Unmatched))continue;
                if(columnOwner[j]<0)columnOwner[j]=i;else Union(parent,columnOwner[j],i);
            }
            // Totals are summed over the whole batch in row order, exactly as a whole-batch
            // solve would, so margins (and hence ambiguity) are bit-identical to it.
            var componentRows=s.Rows;var componentColumns=s.Columns;var rowCost=s.RowCost;var altCost=s.AltCost;
            var done=s.Done;Array.Clear(done,0,rows);
            for(int i=0;i<rows;i++)rowCost[i]=Unmatched;
            int components=0,rowCursor=0,columnCursor=0;
            for(int seed=0;seed<rows;seed++)
            {
                if(done[seed])continue;
                int root=Find(parent,seed),n=0,real=0;
                for(int i=seed;i<rows;i++)if(!done[i]&&Find(parent,i)==root){done[i]=true;componentRows[rowCursor+n++]=i;}
                for(int j=0;j<columns;j++)if(columnOwner[j]>=0&&Find(parent,columnOwner[j])==root)componentColumns[columnCursor+real++]=j;
                s.RowStart[components]=rowCursor;s.RowCount[components]=n;s.ColumnStart[components]=columnCursor;s.ColumnCount[components]=real;components++;
                rowCursor+=n;columnCursor+=real;
            }
            var best=s.Best;var other=s.Other;
            for(int k=0;k<components;k++)
            {
                int r0=s.RowStart[k],n=s.RowCount[k],c0=s.ColumnStart[k],real=s.ColumnCount[k];
                if(real==0)continue;
                var local=s.Local(n,real);Fill(local,costs,componentRows,r0,n,componentColumns,c0,real);
                Optimize(local,n,real,-1,-1,s.Workspace,best,out _);
                for(int r=0;r<n;r++){int row=componentRows[r0+r];result[row]=best[r]<0?-1:componentColumns[c0+best[r]];if(best[r]>=0)rowCost[row]=local[r,best[r]];}
            }
            total=0;for(int i=0;i<rows;i++)total+=rowCost[i];
            for(int k=0;k<components;k++)
            {
                int r0=s.RowStart[k],n=s.RowCount[k],c0=s.ColumnStart[k],real=s.ColumnCount[k];
                if(real==0)continue;
                var local=s.Local(n,real);Fill(local,costs,componentRows,r0,n,componentColumns,c0,real);
                for(int r=0;r<n;r++){int row=componentRows[r0+r];best[r]=result[row]<0?-1:Array.IndexOf(componentColumns,result[row],c0,real)-c0;}
                Array.Copy(rowCost,altCost,rows);
                for(int r=0;r<n;r++)
                {
                    if(best[r]<0)continue;
                    Optimize(local,n,real,r,best[r],s.Workspace,other,out _);
                    for(int i=0;i<n;i++)altCost[componentRows[r0+i]]=other[i]<0?Unmatched:local[i,other[i]];
                    double alternative=0;for(int i=0;i<rows;i++)alternative+=altCost[i];
                    for(int i=0;i<n;i++)altCost[componentRows[r0+i]]=rowCost[componentRows[r0+i]];
                    margins[componentRows[r0+r]]=alternative-total;
                    if(alternative-total<separation)
                        for(int i=0;i<n;i++)if(other[i]!=best[i])ambiguous[componentRows[r0+i]]=true;
                }
            }
            for(int i=0;i<rows;i++)if(ambiguous[i])result[i]=-1;
            return result;
        }

        private static void Fill(double[,] local,double[,] costs,int[] rows,int r0,int n,int[] columns,int c0,int real)
        {for(int r=0;r<n;r++)for(int c=0;c<real;c++)local[r,c]=costs[rows[r0+r],columns[c0+c]];}
        private static int Find(int[] parent,int i){while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i;}
        private static void Union(int[] parent,int a,int b){a=Find(parent,a);b=Find(parent,b);if(a!=b)parent[a<b?b:a]=a<b?a:b;}

        // Per-thread scratch: trackers solve concurrently on worker threads.
        [ThreadStatic] private static Scratch scratch;
        private sealed class Scratch
        {
            public int[] Parent=new int[0],ColumnOwner=new int[0],Rows=new int[0],Columns=new int[0],Best=new int[0],Other=new int[0];
            public int[] RowStart=new int[0],RowCount=new int[0],ColumnStart=new int[0],ColumnCount=new int[0];
            public double[] RowCost=new double[0],AltCost=new double[0];
            public bool[] Done=new bool[0];
            public readonly Workspace Workspace=new Workspace();
            double[,] local=new double[0,0];
            public void Ensure(int rows,int columns)
            {
                if(Parent.Length<rows)
                {
                    Parent=new int[rows];Rows=new int[rows];Best=new int[rows];Other=new int[rows];Done=new bool[rows];
                    RowStart=new int[rows];RowCount=new int[rows];ColumnStart=new int[rows];ColumnCount=new int[rows];RowCost=new double[rows];AltCost=new double[rows];
                }
                if(ColumnOwner.Length<columns){ColumnOwner=new int[columns];Columns=new int[columns];}
            }
            public double[,] Local(int rows,int columns)
            {
                if(local.GetLength(0)<rows||local.GetLength(1)<columns)
                    local=new double[Math.Max(rows,local.GetLength(0)),Math.Max(columns,local.GetLength(1))];
                Workspace.Ensure(rows,columns+rows);
                return local;
            }
        }

        private sealed class Workspace
        {
            public double[] U=new double[0],V=new double[0],Min=new double[0];
            public int[] P=new int[0],Way=new int[0];
            public bool[] Used=new bool[0];
            public void Ensure(int rows,int columns)
            {
                if(U.Length<rows+1)U=new double[rows+1];
                if(V.Length<columns+1){V=new double[columns+1];Min=new double[columns+1];P=new int[columns+1];Way=new int[columns+1];Used=new bool[columns+1];}
            }
        }
        private static void Optimize(double[,] costs,int n,int real,int forbiddenRow,int forbiddenColumn,Workspace workspace,int[] result,out double total)
        {
            int m=real+n;
            var u=workspace.U;var v=workspace.V;var p=workspace.P;var way=workspace.Way;var min=workspace.Min;var used=workspace.Used;
            Array.Clear(u,0,n+1);Array.Clear(v,0,m+1);Array.Clear(p,0,m+1);Array.Clear(way,0,m+1);
            for(int i=1;i<=n;i++)
            {
                p[0]=i;int j0=0;min[0]=0;Array.Clear(used,0,m+1);
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
