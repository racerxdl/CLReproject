OpenCL Reprojection Test
========================

This is a small application I did for testing Image linear reprojection of GOES-16 HRIT Images.
The provided files are for the Full Disk when the satellite was still under test, but already in the highest resolution available in HRIT.

I did that because the reprojection was taking about 30 seconds for a Full Disk image to be processed at my i7 6820HK @ 3.6Ghz. 
With OpenCL (running over CUDA on a GTX980m) it took amazingly 139ms (Including the time for sending and getting the image from GPU).

That will be part of OpenSatelliteProject soon.

PS: Please be kind, that was my first OpenCL program!

It started as just a Reprojection Tool. Now it does False Color Generation too, and under 500 ms!
For anyone that doesn't know what I'm talking about, it basically gets a two images like this:

![Source Image](images/original.jpg "Source Image")

And makes look like this

![Output Image](images/output.jpg "Output Image")
