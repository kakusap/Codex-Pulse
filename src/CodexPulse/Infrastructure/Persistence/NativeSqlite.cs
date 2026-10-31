using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexPulse {
    // History queries use the read-only subclass. Only Pulse owns a
    // writable database. Parameters keep credentials out of SQL and error text.
    public class SqliteDatabase : IDisposable {
        IntPtr db;
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close_v2(IntPtr db);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_name(IntPtr stmt, int col);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_type(IntPtr stmt, int col);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern double sqlite3_column_double(IntPtr stmt, int col);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_bind_null(IntPtr stmt,int index);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_bind_text(IntPtr stmt,int index,byte[] value,int size,IntPtr destructor);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_bind_blob(IntPtr stmt,int index,byte[] value,int size,IntPtr destructor);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_bind_int64(IntPtr stmt,int index,long value);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_bind_double(IntPtr stmt,int index,double value);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_blob(IntPtr stmt,int index);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_bytes(IntPtr stmt,int index);
        static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }
        static string Text(IntPtr p) {
            if (p == IntPtr.Zero) return "";
            int n = 0; while (Marshal.ReadByte(p, n) != 0) n++;
            byte[] b = new byte[n]; Marshal.Copy(p, b, 0, n); return Encoding.UTF8.GetString(b);
        }
        protected SqliteDatabase(string path,bool writable) {
            if (!writable && !File.Exists(path)) throw new FileNotFoundException("未找到本地数据库", path);
            int result = sqlite3_open_v2(Utf8(path), out db, writable?6:1, IntPtr.Zero);
            if (result != 0) { string error = Text(sqlite3_errmsg(db)); Dispose(); throw new IOException(error); }
            sqlite3_busy_timeout(db, 5000);
        }
        public List<Dictionary<string, object>> Query(string sql,params object[] parameters) {
            IntPtr stmt;
            if (sqlite3_prepare_v2(db, Utf8(sql), -1, out stmt, IntPtr.Zero) != 0) throw new IOException(Text(sqlite3_errmsg(db)));
            var rows = new List<Dictionary<string, object>>();
            try {
                for(int index=0;index<parameters.Length;index++) {
                    object value=parameters[index];int bound;
                    if(value==null) bound=sqlite3_bind_null(stmt,index+1);
                    else if(value is byte[]) { byte[] bytes=(byte[])value;bound=sqlite3_bind_blob(stmt,index+1,bytes,bytes.Length,new IntPtr(-1)); }
                    else if(value is string) { byte[] bytes=Encoding.UTF8.GetBytes((string)value);bound=sqlite3_bind_text(stmt,index+1,bytes,bytes.Length,new IntPtr(-1)); }
                    else if(value is double || value is float || value is decimal) bound=sqlite3_bind_double(stmt,index+1,Convert.ToDouble(value,System.Globalization.CultureInfo.InvariantCulture));
                    else bound=sqlite3_bind_int64(stmt,index+1,Convert.ToInt64(value,System.Globalization.CultureInfo.InvariantCulture));
                    if(bound!=0) throw new IOException("SQLite 参数绑定失败");
                }
                int code;
                while ((code = sqlite3_step(stmt)) == 100) {
                    var row = new Dictionary<string, object>();
                    for (int i = 0; i < sqlite3_column_count(stmt); i++) {
                        int type = sqlite3_column_type(stmt, i);
                        object value = null;
                        if (type == 1) value = sqlite3_column_int64(stmt, i);
                        else if (type == 2) value = sqlite3_column_double(stmt, i);
                        else if(type==4) { int size=sqlite3_column_bytes(stmt,i);byte[] bytes=new byte[size];if(size>0) Marshal.Copy(sqlite3_column_blob(stmt,i),bytes,0,size);value=bytes; }
                        else if (type != 5) value = Text(sqlite3_column_text(stmt, i));
                        row[Text(sqlite3_column_name(stmt, i))] = value;
                    }
                    rows.Add(row);
                }
                if (code != 101) throw new IOException(Text(sqlite3_errmsg(db)));
            } finally { sqlite3_finalize(stmt); }
            return rows;
        }
        public bool HasTable(string name) { return Query("SELECT name FROM sqlite_master WHERE type='table' AND name='" + name.Replace("'", "''") + "'").Count > 0; }
        public void Dispose() { if (db != IntPtr.Zero) { sqlite3_close_v2(db); db = IntPtr.Zero; } }
    }
    public sealed class ReadOnlyDb : SqliteDatabase {
        public ReadOnlyDb(string path) : base(path,false) { }
    }
}
