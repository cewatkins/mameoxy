using System;
using System.Data;
using Microsoft.Data.Sqlite;

namespace Spludlow.MameAO
{
	/// <summary>
	/// Minimal replacement for System.Data.SQLite's SQLiteDataAdapter, which has
	/// no counterpart in Microsoft.Data.Sqlite. Only the surface mame-ao uses is
	/// implemented: construct from (sql, connection) or an existing command, then
	/// Fill(DataTable).
	///
	/// NOTE: unlike DataAdapter, we do not dispose a command handed to us by the
	/// caller; the caller keeps ownership. The reader is always disposed.
	/// </summary>
	internal sealed class SqliteDataAdapter : IDisposable
	{
		private readonly SqliteCommand _Command;

		public SqliteDataAdapter(string commandText, SqliteConnection connection)
		{
			_Command = new SqliteCommand(commandText, connection);
		}

		public SqliteDataAdapter(SqliteCommand command)
		{
			_Command = command ?? throw new ArgumentNullException(nameof(command));
		}

		public void Fill(DataTable table)
		{
			SqliteConnection connection = _Command.Connection;

			// DataAdapter.Fill opened a closed connection and closed it again; a
			// SqliteCommand does not. Callers (Database.ExecuteFill) rely on that.
			bool openedHere = false;

			if (connection != null && connection.State != ConnectionState.Open)
			{
				connection.Open();
				openedHere = true;
			}

			try
			{
				using (SqliteDataReader reader = _Command.ExecuteReader())
				{
					table.Load(reader);
				}
			}
			finally
			{
				if (openedHere == true)
					connection.Close();
			}
		}

		public void Dispose()
		{
		}
	}
}
