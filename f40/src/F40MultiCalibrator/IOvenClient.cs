using System;

namespace F40MultiCalibrator;

public interface IOvenClient : IDisposable
{
	void Open();

	void Write(string cmd);

	string Query(string cmd);

	double QueryNumber(string cmd);
}
